#import "present_buffer_observer_v1.h"
#import <QuartzCore/QuartzCore.h>
#import <mach/mach_time.h>
#include "bounded_event_ring.hpp"
#include <atomic>
#include <cmath>
#include <memory>
#include <array>
namespace {
constexpr size_t Capacity=4096;
struct State {
 presented_fixture::BoundedEventRing<PCB1Event,Capacity> events;
 std::atomic<bool> closed{false};
 std::array<std::atomic<uint64_t>,Capacity> instances{};
#define COUNTER(n) std::atomic<uint64_t> n{0};
 COUNTER(attempts) COUNTER(registered) COUNTER(scheduled_registered) COUNTER(completed_registered)
 COUNTER(scheduled_callbacks) COUNTER(completed_callbacks) COUNTER(scheduled_pending) COUNTER(completed_pending)
 COUNTER(scheduled_drops) COUNTER(completed_drops) COUNTER(scheduled_drained) COUNTER(completed_drained)
 COUNTER(identity_failures) COUNTER(gpu_invalid) COUNTER(registration_errors) COUNTER(rejected_after_close)
 COUNTER(duplicate_or_capacity) COUNTER(duplicate_callbacks) COUNTER(active_registrations) COUNTER(callback_exceptions)
#undef COUNTER
};
struct Association {
 std::shared_ptr<State> state;
 uint64_t instance, frame, drawable, pointer;
 uint32_t numer, denom;
 std::atomic<uint32_t> retired{0};
 ~Association(){uint64_t expected=instance;state->instances[instance&(Capacity-1)].compare_exchange_strong(expected,0,std::memory_order_acq_rel);}
};
void observe(const std::shared_ptr<Association>& a, id<MTLCommandBuffer> buffer, uint32_t kind) noexcept {
 auto& s=*a->state;
 PCB1Event e{};
 e.instance=a->instance; e.frame_sequence=a->frame; e.drawable_id=a->drawable;
 e.expected_buffer_pointer=a->pointer; e.kind=kind; e.timebase_numer=a->numer; e.timebase_denom=a->denom;
 e.mach_absolute_before=mach_absolute_time(); e.mach_continuous_before=mach_continuous_time(); e.ca_media_time_before=CACurrentMediaTime();
 @try {
  e.callback_buffer_pointer=(uint64_t)(__bridge void*)buffer;
  e.observed_status=(uint32_t)buffer.status;
  NSError* error=buffer.error;
  e.error_present=error!=nil; e.error_code=error ? (int64_t)error.code : 0;
  if(kind==PCB1Completed) {
   e.gpu_start_time=buffer.GPUStartTime; e.gpu_end_time=buffer.GPUEndTime;
   e.gpu_validity=e.observed_status==MTLCommandBufferStatusError || e.error_present ? PCB1GPUStatusError :
    e.observed_status!=MTLCommandBufferStatusCompleted ? PCB1GPUStatusNotCompleted :
    !std::isfinite(e.gpu_start_time)||!std::isfinite(e.gpu_end_time) ? PCB1GPUNonfinite :
    e.gpu_start_time==0||e.gpu_end_time==0 ? PCB1GPUZero :
    e.gpu_start_time<0||e.gpu_end_time<e.gpu_start_time ? PCB1GPUUnordered : PCB1GPUValid;
  }
 } @catch(NSException*) { e.gpu_validity=PCB1GPUReadException; s.callback_exceptions.fetch_add(1,std::memory_order_relaxed); }
 e.ca_media_time_after=CACurrentMediaTime(); e.mach_continuous_after=mach_continuous_time(); e.mach_absolute_after=mach_absolute_time();
 auto& callbacks=kind==PCB1Scheduled?s.scheduled_callbacks:s.completed_callbacks;
 auto& pending=kind==PCB1Scheduled?s.scheduled_pending:s.completed_pending;
 auto& drops=kind==PCB1Scheduled?s.scheduled_drops:s.completed_drops;
 callbacks.fetch_add(1,std::memory_order_relaxed);
 uint32_t bit=kind==PCB1Scheduled?1:2;
 uint32_t previous=a->retired.fetch_or(bit,std::memory_order_acq_rel);
 if(previous&bit) { s.duplicate_callbacks.fetch_add(1,std::memory_order_relaxed); return; }
 if(e.callback_buffer_pointer!=e.expected_buffer_pointer) s.identity_failures.fetch_add(1,std::memory_order_relaxed);
 if(kind==PCB1Completed && e.gpu_validity!=PCB1GPUValid) s.gpu_invalid.fetch_add(1,std::memory_order_relaxed);
 if(!s.events.tryPush(e)) drops.fetch_add(1,std::memory_order_relaxed);
 pending.fetch_sub(1,std::memory_order_release); // final state access; block owns Association through return
}
}
struct PCB1Observer { std::shared_ptr<State> state; };
extern "C" uint32_t PCB1ABIVersion(){return 1;}
extern "C" size_t PCB1EventSize(){return sizeof(PCB1Event);}
extern "C" size_t PCB1StatsSize(){return sizeof(PCB1Stats);}
extern "C" PCB1Observer* PCB1Create(){try{return new PCB1Observer{std::make_shared<State>()};}catch(...){return nullptr;}}
extern "C" int PCB1Register(PCB1Observer* o,id<MTLCommandBuffer> buffer,id<CAMetalDrawable> drawable,uint64_t instance,uint64_t frame,uint64_t* drawableID){
 if(!o||!buffer||!drawable||!instance||!frame||!drawableID)return PCB1InvalidArgument;
 auto s=o->state; s->attempts.fetch_add(1,std::memory_order_relaxed);
 s->active_registrations.fetch_add(1,std::memory_order_acq_rel);
 struct Leave { State& s; ~Leave(){s.active_registrations.fetch_sub(1,std::memory_order_release);} } leave{*s};
 if(s->closed.load(std::memory_order_acquire)){s->rejected_after_close.fetch_add(1,std::memory_order_relaxed);return PCB1Closed;}
 uint64_t empty=0;
 if(!s->instances[instance&(Capacity-1)].compare_exchange_strong(empty,instance,std::memory_order_acq_rel)){
  s->duplicate_or_capacity.fetch_add(1,std::memory_order_relaxed);return PCB1DuplicateOrCapacity;
 }
 std::shared_ptr<Association> a;
 try { a=std::make_shared<Association>(); } catch(...) {
  s->instances[instance&(Capacity-1)].store(0,std::memory_order_release); s->registration_errors.fetch_add(1,std::memory_order_relaxed); return PCB1RegistrationException;
 }
 a->state=s; a->instance=instance; a->frame=frame; a->pointer=(uint64_t)(__bridge void*)buffer;
 mach_timebase_info_data_t tb{}; mach_timebase_info(&tb); a->numer=tb.numer; a->denom=tb.denom;
 bool attemptedScheduled=false, attemptedCompleted=false;
 @try {
  if(buffer.status>=MTLCommandBufferStatusCommitted) {
   s->instances[instance&(Capacity-1)].store(0,std::memory_order_release);s->registration_errors.fetch_add(1,std::memory_order_relaxed);return PCB1InvalidArgument;
  }
  a->drawable=drawable.drawableID; *drawableID=a->drawable;
  if(!a->drawable){s->instances[instance&(Capacity-1)].store(0,std::memory_order_release);s->registration_errors.fetch_add(1,std::memory_order_relaxed);return PCB1InvalidArgument;}
  s->scheduled_pending.fetch_add(1,std::memory_order_acq_rel);
  s->completed_pending.fetch_add(1,std::memory_order_acq_rel);
  attemptedScheduled=true;
  [buffer addScheduledHandler:^(id<MTLCommandBuffer> callbackBuffer){observe(a,callbackBuffer,PCB1Scheduled);}];
  s->scheduled_registered.fetch_add(1,std::memory_order_relaxed);
  attemptedCompleted=true;
  [buffer addCompletedHandler:^(id<MTLCommandBuffer> callbackBuffer){observe(a,callbackBuffer,PCB1Completed);}];
  s->completed_registered.fetch_add(1,std::memory_order_relaxed);
  s->registered.fetch_add(1,std::memory_order_relaxed);
  return PCB1OK;
 } @catch(NSException*) {
  // A throwing add may already have retained its block. Never cancel its pending credit or free on uncertainty.
  s->registration_errors.fetch_add(1,std::memory_order_relaxed);
  if(attemptedScheduled&&!attemptedCompleted) s->completed_pending.fetch_sub(1,std::memory_order_release);
  if(!attemptedScheduled&&!attemptedCompleted) s->instances[instance&(Capacity-1)].store(0,std::memory_order_release);
  return PCB1RegistrationException;
 }
}
extern "C" void PCB1Close(PCB1Observer* o){if(o)o->state->closed.store(true,std::memory_order_release);}
extern "C" int PCB1ReadStats(const PCB1Observer* o,PCB1Stats* out){if(!o||!out)return PCB1InvalidArgument;
#define READ(n) out->n=o->state->n.load(std::memory_order_acquire);
 READ(attempts) READ(registered) READ(scheduled_registered) READ(completed_registered)
 READ(scheduled_callbacks) READ(completed_callbacks) READ(scheduled_pending) READ(completed_pending)
 READ(scheduled_drops) READ(completed_drops) READ(scheduled_drained) READ(completed_drained)
 READ(identity_failures) READ(gpu_invalid) READ(registration_errors) READ(rejected_after_close)
 READ(duplicate_or_capacity) READ(duplicate_callbacks) READ(active_registrations) READ(callback_exceptions)
#undef READ
 return PCB1OK;}
extern "C" size_t PCB1Drain(PCB1Observer* o,PCB1Event* out,size_t capacity){if(!o||!out)return 0;size_t n=0;while(n<capacity&&o->state->events.tryPop(out[n])){(out[n].kind==PCB1Scheduled?o->state->scheduled_drained:o->state->completed_drained).fetch_add(1,std::memory_order_relaxed);n++;}return n;}
extern "C" int PCB1TryDestroy(PCB1Observer** o){if(!o||!*o)return PCB1InvalidArgument;PCB1Close(*o);auto& s=*(*o)->state;
 if(s.active_registrations.load(std::memory_order_acquire)||s.scheduled_pending.load(std::memory_order_acquire)||s.completed_pending.load(std::memory_order_acquire)||(*o)->state.use_count()!=1)return PCB1Busy; // blocks keep shared context until released after invocation
 if(!s.events.emptyAfterProducersDrain())return PCB1Undrained;
 delete *o;*o=nullptr;return PCB1OK;}
extern "C" size_t PCB1EventOffset(uint32_t i){static const size_t offsets[]={
#define OFFSET(n) offsetof(PCB1Event,n)
 OFFSET(instance),OFFSET(frame_sequence),OFFSET(drawable_id),OFFSET(expected_buffer_pointer),OFFSET(callback_buffer_pointer),OFFSET(mach_absolute_before),OFFSET(mach_continuous_before),OFFSET(ca_media_time_before),OFFSET(mach_continuous_after),OFFSET(mach_absolute_after),OFFSET(ca_media_time_after),OFFSET(gpu_start_time),OFFSET(gpu_end_time),OFFSET(error_code),OFFSET(kind),OFFSET(observed_status),OFFSET(gpu_validity),OFFSET(error_present),OFFSET(timebase_numer),OFFSET(timebase_denom)
#undef OFFSET
 };return i<20?offsets[i]:SIZE_MAX;}
extern "C" size_t PCB1StatsOffset(uint32_t i){return i<20?i*sizeof(uint64_t):SIZE_MAX;}
static_assert(sizeof(PCB1Event)==136,"PCB1 event ABI");
static_assert(sizeof(PCB1Stats)==160,"PCB1 stats ABI");
