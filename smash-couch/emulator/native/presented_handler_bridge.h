#pragma once

#import <QuartzCore/CAMetalLayer.h>
#import <Metal/MTLDrawable.h>
#include <stddef.h>
#include <stdint.h>

typedef struct PHObserver PHObserver;

typedef struct PHEvent
{
    uint64_t frame_sequence;
    uint64_t expected_drawable_id;
    uint64_t callback_drawable_id;
    double presented_time;
    uint64_t mach_absolute_before;
    uint64_t mach_continuous_before;
    double ca_media_time_before;
    uint64_t mach_continuous_after;
    uint64_t mach_absolute_after;
    double ca_media_time_after;
} PHEvent;

#ifdef __cplusplus
extern "C" {
#endif

enum PHStatus
{
    PHStatusOK = 0,
    PHStatusClosed = 1,
    PHStatusInvalidArgument = 2,
    PHStatusBusy = 3, // callbacks still pending
    PHStatusUndrained = 4 // callbacks retired, records remain
};

// The bridge snapshots only scalar IDs/timestamps. It never stores or retains
// the drawable supplied by the caller. Callback state remains alive through a
// block-captured shared_ptr until each callback returns.
// Raw clock domains are deliberately distinct. Managed callers bracket this
// call with Stopwatch ticks; no epoch equivalence is assumed.
typedef struct PHClockSample
{
    uint64_t mach_absolute_before;
    uint64_t mach_continuous_before;
    double ca_media_time_before;
    double ca_media_time_after;
    uint64_t mach_continuous_after;
    uint64_t mach_absolute_after;
    uint32_t timebase_numer;
    uint32_t timebase_denom;
} PHClockSample;
int PHReadClock(PHClockSample* output);
size_t PHEventSize(void);
size_t PHClockSampleSize(void);

PHObserver* PHObserverCreate(void);
int PHObserverRegister(PHObserver* observer, id<CAMetalDrawable> drawable, uint64_t frameSequence);
void PHObserverClose(PHObserver* observer);
uint64_t PHObserverRegistered(const PHObserver* observer);
uint64_t PHObserverCallbacks(const PHObserver* observer);
uint64_t PHObserverPending(const PHObserver* observer);
uint64_t PHObserverIdMismatches(const PHObserver* observer);
uint64_t PHObserverZeroPresentedTime(const PHObserver* observer);
uint64_t PHObserverRejectedAfterClose(const PHObserver* observer);
uint64_t PHObserverDroppedEvents(const PHObserver* observer);
int PHObserverIsDrained(const PHObserver* observer);
size_t PHObserverDrain(PHObserver* observer, PHEvent* output, size_t outputCapacity);
// Succeeds only after close, callback retirement, and draining all captured events.
int PHObserverTryDestroy(PHObserver** observer);

#ifdef __cplusplus
}
#endif
