#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace presented_fixture {

// Bounded MPSC / single-consumer ring. Producers do no allocation, file I/O,
// or blocking lock acquisition. A failed reservation increments dropped().
template <typename T, std::size_t Capacity>
class BoundedEventRing
{
    static_assert(Capacity >= 2 && (Capacity & (Capacity - 1)) == 0,
                  "Capacity must be a power of two");
    static_assert(std::is_trivially_copyable_v<T>,
                  "Callback events must be scalar, trivially copyable data");

    struct Slot
    {
        std::atomic<std::size_t> sequence{0};
        T value{};
    };

    std::array<Slot, Capacity> _slots{};
    alignas(64) std::atomic<std::size_t> _enqueuePosition{0};
    alignas(64) std::size_t _dequeuePosition{0}; // single consumer only
    std::atomic<std::uint64_t> _dropped{0};

public:
    BoundedEventRing()
    {
        for (std::size_t i = 0; i < Capacity; i++)
        {
            _slots[i].sequence.store(i, std::memory_order_relaxed);
        }
    }

    bool tryPush(const T& value) noexcept
    {
        std::size_t position = _enqueuePosition.load(std::memory_order_relaxed);
        Slot* slot;

        for (;;)
        {
            slot = &_slots[position & (Capacity - 1)];
            std::size_t sequence = slot->sequence.load(std::memory_order_acquire);
            std::intptr_t difference = static_cast<std::intptr_t>(sequence) -
                                       static_cast<std::intptr_t>(position);

            if (difference == 0)
            {
                if (_enqueuePosition.compare_exchange_weak(position, position + 1,
                        std::memory_order_relaxed, std::memory_order_relaxed))
                {
                    break;
                }
            }
            else if (difference < 0)
            {
                _dropped.fetch_add(1, std::memory_order_relaxed);
                return false;
            }
            else
            {
                position = _enqueuePosition.load(std::memory_order_relaxed);
            }
        }

        slot->value = value;
        slot->sequence.store(position + 1, std::memory_order_release);
        return true;
    }

    bool tryPop(T& value) noexcept
    {
        const std::size_t position = _dequeuePosition;
        Slot& slot = _slots[position & (Capacity - 1)];
        const std::size_t sequence = slot.sequence.load(std::memory_order_acquire);

        if (sequence != position + 1)
        {
            return false;
        }

        value = slot.value;
        slot.sequence.store(position + Capacity, std::memory_order_release);
        _dequeuePosition = position + 1;
        return true;
    }

    std::uint64_t dropped() const noexcept
    {
        return _dropped.load(std::memory_order_relaxed);
    }

    bool emptyAfterProducersDrain() const noexcept
    {
        return _dequeuePosition == _enqueuePosition.load(std::memory_order_acquire);
    }

    static constexpr std::size_t capacity() noexcept { return Capacity; }
};

} // namespace presented_fixture
