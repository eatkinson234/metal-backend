#import "presented_handler_bridge.h"
#import <QuartzCore/QuartzCore.h>
#import <mach/mach_time.h>

#include "bounded_event_ring.hpp"

#include <atomic>
#include <cmath>
#include <memory>

namespace
{
using EventRing = presented_fixture::BoundedEventRing<PHEvent, 4096>;

struct State
{
    EventRing events;
    std::atomic<bool> closed{false};
    std::atomic<std::uint64_t> registered{0};
    std::atomic<std::uint64_t> callbacks{0};
    std::atomic<std::uint64_t> pending{0};
    std::atomic<std::uint64_t> idMismatches{0};
    std::atomic<std::uint64_t> zeroPresentedTime{0};
    std::atomic<std::uint64_t> rejectedAfterClose{0};
};
} // namespace

struct PHObserver
{
    std::shared_ptr<State> state;
};

extern "C" PHObserver* PHObserverCreate(void)
{
    try
    {
        return new PHObserver{std::make_shared<State>()};
    }
    catch (...)
    {
        return nullptr;
    }
}

extern "C" int PHObserverRegister(PHObserver* observer, id<CAMetalDrawable> drawable,
                                   uint64_t frameSequence)
{
    if (observer == nullptr || drawable == nil)
    {
        return PHStatusInvalidArgument;
    }

    std::shared_ptr<State> state = observer->state;
    if (state->closed.load(std::memory_order_acquire))
    {
        state->rejectedAfterClose.fetch_add(1, std::memory_order_relaxed);
        return PHStatusClosed;
    }

    state->pending.fetch_add(1, std::memory_order_acq_rel);
    if (state->closed.load(std::memory_order_acquire))
    {
        state->pending.fetch_sub(1, std::memory_order_acq_rel);
        state->rejectedAfterClose.fetch_add(1, std::memory_order_relaxed);
        return PHStatusClosed;
    }

    // Save the scalar before creating the block. The block intentionally does
    // not capture drawable; Metal passes the presented drawable to the callback.
    const uint64_t expectedDrawableID = drawable.drawableID;
    [drawable addPresentedHandler:^(id<MTLDrawable> presentedDrawable) {
        PHEvent event{};
        event.frame_sequence = frameSequence;
        event.expected_drawable_id = expectedDrawableID;
        event.mach_absolute_before = mach_absolute_time();
        event.mach_continuous_before = mach_continuous_time();
        event.ca_media_time_before = CACurrentMediaTime();
        event.callback_drawable_id = presentedDrawable.drawableID;
        event.presented_time = presentedDrawable.presentedTime;
        event.ca_media_time_after = CACurrentMediaTime();
        event.mach_continuous_after = mach_continuous_time();
        event.mach_absolute_after = mach_absolute_time();

        state->callbacks.fetch_add(1, std::memory_order_relaxed);
        if (event.callback_drawable_id != event.expected_drawable_id)
        {
            state->idMismatches.fetch_add(1, std::memory_order_relaxed);
        }
        if (!std::isfinite(event.presented_time) || event.presented_time == 0.0)
        {
            state->zeroPresentedTime.fetch_add(1, std::memory_order_relaxed);
        }

        state->events.tryPush(event); // bounded; increments the ring's drop counter on overflow
        state->pending.fetch_sub(1, std::memory_order_release); // final state access in callback
    }];

    state->registered.fetch_add(1, std::memory_order_relaxed);
    return PHStatusOK;
}

extern "C" void PHObserverClose(PHObserver* observer)
{
    if (observer != nullptr)
    {
        observer->state->closed.store(true, std::memory_order_release);
    }
}

extern "C" uint64_t PHObserverRegistered(const PHObserver* observer)
{
    return observer ? observer->state->registered.load(std::memory_order_relaxed) : 0;
}

extern "C" uint64_t PHObserverCallbacks(const PHObserver* observer)
{
    return observer ? observer->state->callbacks.load(std::memory_order_relaxed) : 0;
}

extern "C" uint64_t PHObserverPending(const PHObserver* observer)
{
    return observer ? observer->state->pending.load(std::memory_order_acquire) : 0;
}

extern "C" uint64_t PHObserverIdMismatches(const PHObserver* observer)
{
    return observer ? observer->state->idMismatches.load(std::memory_order_relaxed) : 0;
}

extern "C" uint64_t PHObserverZeroPresentedTime(const PHObserver* observer)
{
    return observer ? observer->state->zeroPresentedTime.load(std::memory_order_relaxed) : 0;
}

extern "C" uint64_t PHObserverRejectedAfterClose(const PHObserver* observer)
{
    return observer ? observer->state->rejectedAfterClose.load(std::memory_order_relaxed) : 0;
}

extern "C" uint64_t PHObserverDroppedEvents(const PHObserver* observer)
{
    return observer ? observer->state->events.dropped() : 0;
}

extern "C" int PHObserverIsDrained(const PHObserver* observer)
{
    return observer && observer->state->pending.load(std::memory_order_acquire) == 0 &&
           observer->state->events.emptyAfterProducersDrain();
}

extern "C" size_t PHObserverDrain(PHObserver* observer, PHEvent* output, size_t outputCapacity)
{
    if (observer == nullptr || output == nullptr)
    {
        return 0;
    }

    size_t count = 0;
    while (count < outputCapacity && observer->state->events.tryPop(output[count]))
    {
        count++;
    }
    return count;
}

extern "C" int PHObserverTryDestroy(PHObserver** observer)
{
    if (observer == nullptr || *observer == nullptr)
    {
        return PHStatusInvalidArgument;
    }

    PHObserverClose(*observer);
    const auto& state = (*observer)->state;
    if (state->pending.load(std::memory_order_acquire) != 0)
    {
        return PHStatusBusy;
    }
    if (!state->events.emptyAfterProducersDrain())
    {
        return PHStatusUndrained;
    }

    delete *observer;
    *observer = nullptr;
    return PHStatusOK;
}

static_assert(sizeof(PHEvent) == 80, "Presented event ABI changed");
static_assert(sizeof(PHClockSample) == 56, "Clock sample ABI changed");

extern "C" size_t PHEventSize(void) { return sizeof(PHEvent); }
extern "C" size_t PHClockSampleSize(void) { return sizeof(PHClockSample); }
extern "C" int PHReadClock(PHClockSample* output)
{
    if (output == nullptr) return PHStatusInvalidArgument;
    mach_timebase_info_data_t timebase{};
    if (mach_timebase_info(&timebase) != KERN_SUCCESS || timebase.denom == 0)
        return PHStatusInvalidArgument;
    output->timebase_numer = timebase.numer;
    output->timebase_denom = timebase.denom;
    output->mach_absolute_before = mach_absolute_time();
    output->mach_continuous_before = mach_continuous_time();
    output->ca_media_time_before = CACurrentMediaTime();
    output->ca_media_time_after = CACurrentMediaTime();
    output->mach_continuous_after = mach_continuous_time();
    output->mach_absolute_after = mach_absolute_time();
    return PHStatusOK;
}
