import SwiftUI

struct ControlBindingsView: View {
    let layout: ControllerLayout
    let family: ControllerFamily
    var body: some View {
        VStack(spacing: 0) {
            ForEach(SmashAction.allCases) { action in
                HStack(spacing: 16) {
                    Text(action.title).font(.system(size: 16, weight: .semibold))
                    Spacer()
                    let buttons = layout.buttons(for: action)
                    ForEach(Array(buttons.enumerated()), id: \.element) { offset, button in
                        if offset > 0 { Text("or").font(.system(size: 12)).foregroundStyle(CouchTheme.secondary) }
                        ButtonBadge(text: family.label(button))
                    }
                }
                .padding(.vertical, 7)
                .accessibilityElement(children: .ignore)
                .accessibilityAddTraits(.isStaticText)
                .accessibilityLabel("\(action.title): \(layout.buttons(for: action).map { family.spokenLabel($0) }.joined(separator: " or "))")
                if action != .special { Divider().overlay(CouchTheme.line) }
            }
        }
    }
}

struct ButtonBadge: View {
    let text: String
    var body: some View {
        Text(text).font(.system(size: 16, weight: .bold, design: .rounded))
            .frame(minWidth: 24).padding(.horizontal, 8).padding(.vertical, 6)
            .foregroundStyle(CouchTheme.text)
            .background(CouchTheme.elevated, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(CouchTheme.line))
    }
}
