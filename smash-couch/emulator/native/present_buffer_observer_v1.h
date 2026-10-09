#pragma once
#import <Metal/MTLCommandBuffer.h>
#import <QuartzCore/CAMetalLayer.h>
#include <stdint.h>
#include <stddef.h>
typedef struct PCB1Observer PCB1Observer;
enum PCB1Status { PCB1OK=0, PCB1Closed=1, PCB1InvalidArgument=2, PCB1Busy=3, PCB1Undrained=4, PCB1DuplicateOrCapacity=5, PCB1RegistrationException=6 };
enum PCB1Kind { PCB1Scheduled=1, PCB1Completed=2 };
enum PCB1Validity { PCB1GPUNotRead=0, PCB1GPUValid=1, PCB1GPUStatusError=2, PCB1GPUStatusNotCompleted=3, PCB1GPUZero=4, PCB1GPUNonfinite=5, PCB1GPUUnordered=6, PCB1GPUReadException=7 };
// Versioned, scalar-only ABI. Apple GPU host seconds remain unmapped system-mach seconds.
typedef struct PCB1Event {
 uint64_t instance, frame_sequence, drawable_id, expected_buffer_pointer, callback_buffer_pointer;
 uint64_t mach_absolute_before, mach_continuous_before;
 double ca_media_time_before;
 uint64_t mach_continuous_after, mach_absolute_after;
 double ca_media_time_after, gpu_start_time, gpu_end_time;
 int64_t error_code;
 uint32_t kind, observed_status, gpu_validity, error_present, timebase_numer, timebase_denom;
} PCB1Event;
typedef struct PCB1Stats {
 uint64_t attempts, registered, scheduled_registered, completed_registered;
 uint64_t scheduled_callbacks, completed_callbacks, scheduled_pending, completed_pending;
 uint64_t scheduled_drops, completed_drops, scheduled_drained, completed_drained;
 uint64_t identity_failures, gpu_invalid, registration_errors, rejected_after_close;
 uint64_t duplicate_or_capacity, duplicate_callbacks, active_registrations, callback_exceptions;
} PCB1Stats;
#ifdef __cplusplus
extern "C" {
#endif
uint32_t PCB1ABIVersion(void);
size_t PCB1EventSize(void);
size_t PCB1StatsSize(void);
// Field offsets indexed in declaration order, independently compared by managed loader.
size_t PCB1EventOffset(uint32_t field);
size_t PCB1StatsOffset(uint32_t field);
PCB1Observer* PCB1Create(void);
// Caller serializes destruction against API admission, as existing managed registration gate does.
// At most one presentation per instance. Active slot collisions refuse explicitly.
int PCB1Register(PCB1Observer*, id<MTLCommandBuffer>, id<CAMetalDrawable>, uint64_t instance, uint64_t frame, uint64_t* drawableID);
void PCB1Close(PCB1Observer*);
int PCB1ReadStats(const PCB1Observer*, PCB1Stats*);
size_t PCB1Drain(PCB1Observer*, PCB1Event*, size_t capacity);
int PCB1TryDestroy(PCB1Observer**);
#ifdef __cplusplus
}
#endif
