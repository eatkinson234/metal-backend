import Foundation

struct SetupError: LocalizedError {
    var message: String
    var errorDescription: String? { message }
    init(_ message: String) { self.message = message }
}
struct CouchConfiguration {
    static func validate(_ x: [String:Any]) throws {
        guard x["version"] as? Int == 70,
              x["game_dirs"] is [String], x["input_config"] is [[String:Any]],
              x["autoload_dirs"] is [String], x["vsync_mode"] as? Int == 0,
              x["enable_custom_vsync_interval"] as? Bool == false,
              x["graphics_backend"] as? String == "Vulkan",
              x["docked_mode"] as? Bool == true,
              x["use_hypervisor"] as? Bool == true else {
            throw SetupError("Expected Ryujinx schema 70 with Vulkan, docked mode, hypervisor and normal Switch VSync. No settings were written.")
        }
    }
    static func player(_ i: Int, _ p: PlayerProfile) -> [String:Any] {
        let positional = p.layout != "letters"
        return ["version":1,"backend":"GamepadSDL2","id":"couch-\(i+1)","name":p.label,"controller_type":"ProController","player_index":"Player\(i+1)",
            "deadzone_left":0.0,"deadzone_right":0.0,"range_left":1.0,"range_right":1.0,"trigger_threshold":0.5,
            "motion":["motion_backend":"GamepadDriver","enable_motion":false,"sensitivity":100,"gyro_deadzone":1.0],
            "rumble":["enable_rumble":false,"strong_rumble":1.0,"weak_rumble":1.0],
            "led":["enable_led":false,"turn_off_led":false,"use_rainbow":false,"led_color":0],
            "left_joycon_stick":["joystick":"Left","stick_button":"LeftStick","invert_stick_x":false,"invert_stick_y":false,"rotate90_cw":false],
            "right_joycon_stick":["joystick":"Right","stick_button":"RightStick","invert_stick_x":false,"invert_stick_y":false,"rotate90_cw":false],
            "left_joycon":["button_minus":"Minus","button_l":"LeftShoulder","button_zl":"LeftTrigger","button_sl":"Unbound","button_sr":"Unbound","dpad_up":"DpadUp","dpad_down":"DpadDown","dpad_left":"DpadLeft","dpad_right":"DpadRight"],
            "right_joycon":["button_plus":"Plus","button_r":"RightShoulder","button_zr":"RightTrigger","button_sl":"Unbound","button_sr":"Unbound","button_a":positional ? "B":"A","button_b":positional ? "A":"B","button_x":positional ? "Y":"X","button_y":positional ? "X":"Y"]]
    }
    static func prepare(data: Data, profiles: [PlayerProfile], capacity: Int, muted: Bool, fullScreen: Bool, lastAudibleVolume: Double = 1.0) throws -> Data {
        guard lastAudibleVolume.isFinite, lastAudibleVolume>0, lastAudibleVolume<=1,
              (1...8).contains(capacity), profiles.count == 8,
              profiles.allSatisfy({ ControllerLayout(rawValue: $0.layout) != nil && !$0.label.trimmingCharacters(in:.whitespaces).isEmpty && $0.label.count <= 40 }),
              var x = try JSONSerialization.jsonObject(with:data) as? [String:Any] else { throw SetupError("Invalid player profiles or configuration.") }
        try validate(x)
        x["input_config"] = (0..<capacity).map { player($0,profiles[$0]) }
        x["use_input_global_config"] = true
        x["audio_volume"] = muted ? 0.0 : ((x["audio_volume"] as? Double).flatMap { $0 > 0 ? $0 : nil } ?? lastAudibleVolume)
        x["start_fullscreen"] = fullScreen
        // Preserve every other option, including unknown future fields.
        return try JSONSerialization.data(withJSONObject:x,options:[.prettyPrinted,.sortedKeys])
    }
}

struct SessionPreferences: Codable {
    var version=1
    var capacity=1
    var muted=false
    var fullscreen=true
    var lastAudibleVolume=1.0
    var isValid: Bool {
        version==1 && (1...8).contains(capacity) && lastAudibleVolume.isFinite && lastAudibleVolume>0 && lastAudibleVolume<=1
    }
}
struct DiagnosticEnvironment {
    static func prepare(_ inherited:[String:String], hud:Bool, capture:Bool)->[String:String] {
        var result=inherited
        result.removeValue(forKey:"MTL_HUD_ENABLED")
        result.removeValue(forKey:"MTL_CAPTURE_ENABLED")
        if hud { result["MTL_HUD_ENABLED"]="1" }
        if capture { result["MTL_CAPTURE_ENABLED"]="1" }
        return result
    }
}
