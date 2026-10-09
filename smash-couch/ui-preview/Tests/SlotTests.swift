import Foundation
@main struct Tests {
 static func main() throws {
  if CommandLine.arguments.contains("--axis-negative") {
   var malformed=SlotBook();malformed.capacity=2;let a=UUID();let b=UUID()
   precondition(malformed.join(a,at:0) && malformed.join(b,at:1),"Malformed-axis setup join")
   malformed.update(a,sample:.neutral);malformed.update(a,sample:PadSample(buttons:1<<1,axes:[0,0,0,0,0,0]))
   malformed.update(b,sample:.neutral);malformed.update(b,sample:PadSample(buttons:1<<2,axes:[0,0,0,0,0,0]))
   malformed.slots[0].sample.axes=Array(repeating:0,count:5)
   malformed.slots[1].sample.axes=Array(repeating:0,count:7)
   _=malformed.packet(token:Array(repeating:1,count:16),sequence:1,captureTime:1,enabled:true)
   fatalError("Compensating five-plus-seven axis packet was accepted")
  }
  var count=0
  func check(_ ok:Bool,_ text:String){precondition(ok,text);count+=1}
  check(PlayerProfile(label:"Default").layout=="positions","Default follows Nintendo physical positions")
  var book=SlotBook();book.capacity=8
  let ids=(0..<8).map{_ in UUID()}
  for i in 0..<8{check(book.join(ids[i],at:i),"Join distinct logical slot")}
  check(!book.join(UUID(),at:0),"Cannot steal occupied slot")
  check(!book.join(ids[0],at:7),"One controller cannot own two slots")
  var held=PadSample();held.buttons=1<<1;held.axes[0]=1
  book.update(ids[0],sample:held);check(book.slots[0].sample == .neutral,"Join press suppressed")
  book.update(ids[0],sample:.neutral);book.update(ids[0],sample:held);check(book.slots[0].sample==held,"Input after release")
  book.disconnect(ids[0]);check(book.slots[0].sample == .neutral && book.slots[0].reserved,"Disconnect releases inputs and reserves slot")
  for i in 1..<8{check(book.slots[i].connection==ids[i],"Remaining player never shifts")}
  let replacement=UUID();check(book.join(replacement,at:0),"Explicit rejoin into reserved slot")
  check(!book.slots[0].ready,"Rejoin needs neutral release")
  book.update(replacement,sample:.neutral);book.update(replacement,sample:held)
  book.suspend();check(book.slots.allSatisfy{$0.sample == .neutral && !$0.ready},"Focus loss neutralizes")
  let packetToken=Array(UInt8(1)...UInt8(16));let packetTime:UInt64=0x0102030405060708
  let packet=book.packet(token:packetToken,sequence:1,captureTime:packetTime,enabled:true)
  check(packet.count==296 && packet.count==SlotBook.packetBytes,"Protocol v2 exact size")
  func word64(_ data:Data,_ at:Int)->UInt64 { var result:UInt64=0;for i in 0..<8{result |= UInt64(data[at+i]) << UInt64(i*8)};return result }
  check(word64(packet,0)==SlotBook.packetMagicV2,"Protocol v2 magic")
  check(Array(packet[8..<24])==packetToken && word64(packet,24)==1 && word64(packet,32)==packetTime,"Versioned header fields")
  let muted=book.packet(token:packetToken,sequence:2,captureTime:packetTime,enabled:false)
  check(muted.dropFirst(SlotBook.packetHeaderBytes).allSatisfy{$0==0},"Inactive process sends neutral packet")
  let profiles=(1...8).map{PlayerProfile(label:"Pad \($0)",layout:"positions")}
  let config:[String:Any] = ["version":70,"game_dirs":["/game"],"autoload_dirs":[],"input_config":[],"vsync_mode":0,"enable_custom_vsync_interval":false,"graphics_backend":"Vulkan","docked_mode":true,"use_hypervisor":true,"unknown_option":["retain":73],"audio_volume":0.8,"res_scale":1]
  let data=try JSONSerialization.data(withJSONObject:config)
  for n in 1...8 {
   let out=try CouchConfiguration.prepare(data:data,profiles:profiles,capacity:n,muted:true,fullScreen:false)
   let x=try JSONSerialization.jsonObject(with:out) as! [String:Any]
   let inputs=x["input_config"] as! [[String:Any]]
   check(inputs.count==n,"Correct slot capacity")
   check(Set(inputs.map{$0["id"] as! String}).count==n,"Distinct IDs")
   check(Set(inputs.map{$0["player_index"] as! String}).count==n,"Distinct logical players")
   check((x["unknown_option"] as? [String:Int])?["retain"]==73,"Unknown options preserved")
  }
  var wrong=config;wrong["version"]=71
  do{_ = try CouchConfiguration.prepare(data:JSONSerialization.data(withJSONObject:wrong),profiles:profiles,capacity:4,muted:true,fullScreen:false);fatalError("Schema drift accepted")}catch{count+=1}
  wrong=config;wrong["vsync_mode"]=1
  do{_ = try CouchConfiguration.prepare(data:JSONSerialization.data(withJSONObject:wrong),profiles:profiles,capacity:4,muted:true,fullScreen:false);fatalError("Unbounded speed accepted")}catch{count+=1}
  var letters=profiles;letters[0].layout="letters"
  let letterMap=CouchConfiguration.player(0,letters[0])["right_joycon"] as! [String:String]
  let positionMap=CouchConfiguration.player(0,profiles[0])["right_joycon"] as! [String:String]
  check(letterMap["button_a"]=="A" && letterMap["button_b"]=="B" && letterMap["button_x"]=="X" && letterMap["button_y"]=="Y","Xbox letter matching")
  check(positionMap["button_a"]=="B" && positionMap["button_b"]=="A","Position profile remains distinct")
  var extra=profiles;extra[0].layout="positions_x_attack"
  let custom=try CouchConfiguration.prepare(data:data,profiles:extra,capacity:2,muted:false,fullScreen:true)
  let customConfig=try JSONSerialization.jsonObject(with:custom) as! [String:Any]
  let customMap=(customConfig["input_config"] as! [[String:Any]])[0]["right_joycon"] as! [String:String]
  check(customMap==positionMap,"X-attack profile keeps Nintendo positional mapping")
  for bits:UInt32 in [0,1<<1,1<<2,1<<3,1<<4,(1<<2)|(1<<3),(1<<1)|(1<<3)|(1<<4)] {
   let raw=PadSample(buttons:bits,axes:[0.2,-0.5,0,1,0.3,0.4])
   let mapped=raw.mapped(for:"positions_x_attack")
   check(mapped.down(2)==(raw.down(2)||raw.down(3)),"Either B or X holds Attack")
   check(!mapped.down(3),"X no longer also presses Jump")
   check(mapped.down(1)==raw.down(1) && mapped.down(4)==raw.down(4) && mapped.axes==raw.axes,"Special, Y Jump and axes preserved")
   check(raw.mapped(for:"positions")==raw && raw.mapped(for:"letters")==raw,"Other profiles unchanged")
  }
  check(PadSample.neutral.mapped(for:"positions_x_attack").isNeutral,"Release stays neutral")
  var startBook=SlotBook();let pad=UUID();let menu=PadSample(buttons:1<<16)
  check(!startBook.canLaunch(pad,current:menu,previous:.neutral),"Unjoined pad cannot launch")
  _=startBook.join(pad,at:0)
  check(!startBook.canLaunch(pad,current:menu,previous:.neutral),"Join press cannot also launch")
  startBook.update(pad,sample:.neutral)
  check(startBook.canLaunch(pad,current:menu,previous:.neutral),"Fresh Menu after release launches")
  check(!startBook.canLaunch(pad,current:menu,previous:menu),"Held Menu cannot relaunch")
  check(!startBook.canLaunch(pad,current:PadSample(buttons:(1<<16)|(1<<1)),previous:.neutral),"Button chord cannot accidentally launch")
  startBook.suspend();check(!startBook.canLaunch(pad,current:menu,previous:.neutral),"Focus suspension requires release again")
  let pref=SessionPreferences(capacity:4,muted:true,fullscreen:false,lastAudibleVolume:0.35)
  let roundTrip=try JSONDecoder().decode(SessionPreferences.self,from:JSONEncoder().encode(pref))
  check(roundTrip.isValid && roundTrip.capacity==4 && roundTrip.muted && !roundTrip.fullscreen && roundTrip.lastAudibleVolume==0.35,"Session preferences round trip")
  check(!SessionPreferences(capacity:9).isValid && !SessionPreferences(lastAudibleVolume:0).isValid,"Invalid stored session rejected")
  var zero=config;zero["audio_volume"]=0.0
  let restored=try CouchConfiguration.prepare(data:JSONSerialization.data(withJSONObject:zero),profiles:profiles,capacity:1,muted:false,fullScreen:true,lastAudibleVolume:0.35)
  check((try JSONSerialization.jsonObject(with:restored) as! [String:Any])["audio_volume"] as? Double == 0.35,"Unmute restores remembered audible level")
  let env=["MTL_HUD_ENABLED":"1","MTL_CAPTURE_ENABLED":"1","KEEP":"unchanged"]
  check(DiagnosticEnvironment.prepare(env,hud:false,capture:false)==["KEEP":"unchanged"],"Disabled diagnostics remove inherited flags")
  check(DiagnosticEnvironment.prepare(env,hud:true,capture:false)==["KEEP":"unchanged","MTL_HUD_ENABLED":"1"],"Diagnostic toggles independent")
  if let i=CommandLine.arguments.firstIndex(of:"--golden"),i+1<CommandLine.arguments.count {
   var golden=SlotBook();golden.capacity=8;let gids=(0..<8).map{_ in UUID()}
   for slot in 0..<8 {
    check(golden.join(gids[slot],at:slot),"Golden packet slot join")
    golden.update(gids[slot],sample:.neutral)
    golden.update(gids[slot],sample:PadSample(buttons:1<<UInt32(slot+1),axes:[Float(slot)*0.1,-0.5,0.25,Float(slot-4)*0.1,0.2,0.4]))
   }
   let bytes=golden.packet(token:packetToken,sequence:0x1122334455667788,captureTime:0x0102030405060708,enabled:true)
   try bytes.write(to:URL(fileURLWithPath:CommandLine.arguments[i+1]),options:.atomic)
   check(bytes.count==SlotBook.packetBytes,"Golden packet byte count")
  }
  print("PASS: \(count) assignment, disconnect, rejoin, focus, packet and schema assertions. Synthetic checks; not physical-controller acceptance.")
 }
}
