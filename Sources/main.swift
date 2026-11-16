//
//  OpenCode 红绿灯 v2
//  在 v1 的基础上调整灯语：
//    · 失败              → 红快闪（原为红常亮）
//    · 权限请求          → 红常亮（不变）
//    · 完成未查看        → 绿柔闪，90 秒后转绿常亮（新增）
//    · 等你确认          → 黄呼吸（不变）
//    · 进行中            → 黄常亮（不变）
//    · 空闲              → 绿常亮（不变）
//

import AppKit
import Foundation

// MARK: - 常量

let kAppName = "OpenCodeTrafficLight"
let kWindowWidth: CGFloat = 52
let kWindowHeight: CGFloat = 127
let kLampDiameter: CGFloat = 31
let kLampGap: CGFloat = 5

let kFailureWindow: TimeInterval = 600      // 失败后红灯保持 10 分钟
let kDoneWindow: TimeInterval = 90          // 完成未查看的绿闪持续 90 秒
let kPollInterval: TimeInterval = 1.2

// MARK: - 模型

struct ServiceConfig {
    let baseURL: URL
    let password: String

    static func load() -> ServiceConfig? {
        let path = ("~/.local/state/opencode/service.json" as NSString).expandingTildeInPath
        guard let data = FileManager.default.contents(atPath: path),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let urlString = obj["url"] as? String,
              let url = URL(string: urlString),
              let password = obj["password"] as? String
        else { return nil }
        return ServiceConfig(baseURL: url, password: password)
    }
}

struct SessionInfo {
    let id: String
    let title: String
    let parentID: String?
    let updated: Double          // 毫秒
    let idle: Double?
    let viewed: Double?
    let outcome: String?

    init?(_ json: [String: Any]) {
        guard let id = json["id"] as? String else { return nil }
        self.id = id
        self.title = (json["title"] as? String) ?? "(未命名会话)"
        self.parentID = json["parentID"] as? String
        let time = (json["time"] as? [String: Any]) ?? [:]
        guard let updated = (time["updated"] as? NSNumber)?.doubleValue else { return nil }
        self.updated = updated
        self.idle = (time["idle"] as? NSNumber)?.doubleValue
        self.viewed = (time["viewed"] as? NSNumber)?.doubleValue
        self.outcome = json["outcome"] as? String
    }
}

enum LightMode { case green, yellow, red }

enum BlinkKind: Equatable {
    case none
    case yellowPulse   // 等你确认：1.6s 呼吸
    case redFast       // 失败：0.5s 快闪（2Hz）
    case greenSoft     // 完成未查看：1.0s 柔和呼吸

    var isBlinking: Bool { self != .none }
}

struct LightState: Equatable {
    var mode: LightMode = .red
    var blink: BlinkKind = .none
    var headline: String = "正在连接 OpenCode"
    var detail: String = ""
    var connected: Bool = false
    var running: Int = 0
    var perms: Int = 0
    var forms: Int = 0
}

// MARK: - HTTP

enum API {
    static func get(_ cfg: ServiceConfig, _ path: String, timeout: TimeInterval = 2.0) -> Any? {
        guard let url = URL(string: cfg.baseURL.absoluteString + path) else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        request.cachePolicy = .reloadIgnoringLocalCacheData
        let token = Data("opencode:\(cfg.password)".utf8).base64EncodedString()
        request.setValue("Basic \(token)", forHTTPHeaderField: "Authorization")

        let semaphore = DispatchSemaphore(value: 0)
        let lock = NSLock()
        var result: Any?
        URLSession.shared.dataTask(with: request) { data, response, _ in
            defer { semaphore.signal() }
            guard let data,
                  let http = response as? HTTPURLResponse,
                  (200..<300).contains(http.statusCode),
                  let object = try? JSONSerialization.jsonObject(with: data)
            else { return }
            lock.lock(); result = object; lock.unlock()
        }.resume()

        if semaphore.wait(timeout: .now() + timeout + 0.6) == .timedOut { return nil }
        lock.lock(); defer { lock.unlock() }
        return result
    }
}

// MARK: - 状态判定

enum Monitor {

    static func snapshot() -> LightState {
        guard let cfg = ServiceConfig.load() else {
            return LightState(mode: .red, blink: .none,
                              headline: "未找到 OpenCode 服务",
                              detail: "请先启动 OpenCode。",
                              connected: false)
        }

        guard let activeRaw = API.get(cfg, "/api/session/active") as? [String: Any],
              let activeData = activeRaw["data"] as? [String: Any]
        else {
            return LightState(mode: .red, blink: .none,
                              headline: "无法读取 OpenCode 状态",
                              detail: "OpenCode 服务可能已退出或端口发生变化。",
                              connected: false)
        }

        guard let sessionsRaw = API.get(cfg, "/api/session?limit=25&order=desc") as? [String: Any],
              let sessionList = sessionsRaw["data"] as? [[String: Any]]
        else {
            return LightState(mode: .red, blink: .none,
                              headline: "无法读取 OpenCode 状态",
                              detail: "会话列表读取失败。",
                              connected: false)
        }

        let runningIDs = Set(activeData.keys)
        let sessions = sessionList.compactMap(SessionInfo.init)
        let now = Date().timeIntervalSince1970 * 1000

        // 只在“最可能产生待办”的会话里查权限/表单
        var candidates: [String] = Array(runningIDs)
        for s in sessions where !runningIDs.contains(s.id) {
            if candidates.count >= 8 { break }
            candidates.append(s.id)
        }

        var permissions: [(action: String, session: String)] = []
        var forms: [(title: String, session: String)] = []
        let lock = NSLock()
        let group = DispatchGroup()

        for sid in candidates.prefix(8) {
            group.enter()
            DispatchQueue.global(qos: .utility).async {
                defer { group.leave() }

                if let raw = API.get(cfg, "/api/session/\(sid)/permission") as? [String: Any],
                   let list = raw["data"] as? [[String: Any]] {
                    let title = sessions.first(where: { $0.id == sid })?.title ?? ""
                    for item in list {
                        let action = (item["action"] as? String) ?? "确认"
                        lock.lock()
                        permissions.append((action, title))
                        lock.unlock()
                    }
                }

                if let raw = API.get(cfg, "/api/session/\(sid)/form") as? [String: Any],
                   let list = raw["data"] as? [[String: Any]], !list.isEmpty {
                    let title = sessions.first(where: { $0.id == sid })?.title ?? ""
                    for item in list {
                        guard let fid = item["id"] as? String else { continue }
                        // 列表不含 state，需要详情确认仍在等待
                        guard let detail = API.get(cfg, "/api/session/\(sid)/form/\(fid)") as? [String: Any],
                              let data = detail["data"] as? [String: Any],
                              let state = data["state"] as? [String: Any],
                              (state["status"] as? String) == "pending"
                        else { continue }
                        let formTitle = (item["title"] as? String) ?? "确认请求"
                        lock.lock()
                        forms.append((formTitle, title))
                        lock.unlock()
                    }
                }
            }
        }
        _ = group.wait(timeout: .now() + 2.8)

        // 1) 权限请求 → 红常亮
        if !permissions.isEmpty {
            let detail: String
            if permissions.count == 1 {
                detail = "权限请求：\(permissions[0].action)\n会话：\(permissions[0].session)"
            } else {
                detail = "\(permissions.count) 个权限请求待处理"
            }
            return LightState(mode: .red, blink: .none,
                              headline: "需要你处理", detail: detail,
                              connected: true, running: runningIDs.count,
                              perms: permissions.count, forms: forms.count)
        }

        // 2) 等你确认 → 黄呼吸
        if let form = forms.first {
            return LightState(mode: .yellow, blink: .yellowPulse,
                              headline: "等待你确认",
                              detail: "确认请求：\(form.title)\n会话：\(form.session)",
                              connected: true, running: runningIDs.count,
                              perms: 0, forms: forms.count)
        }

        // 3) 有会话在跑 → 黄常亮
        if !runningIDs.isEmpty {
            let titles = sessions.filter { runningIDs.contains($0.id) }.prefix(6).map { "• \($0.title)" }
            var detail = "\(runningIDs.count) 个会话正在运行"
            if !titles.isEmpty { detail += "\n" + titles.joined(separator: "\n") }
            return LightState(mode: .yellow, blink: .none,
                              headline: "任务进行中", detail: detail,
                              connected: true, running: runningIDs.count,
                              perms: 0, forms: 0)
        }

        // 顶层最近会话
        let latest = sessions.first(where: { $0.parentID == nil }) ?? sessions.first

        // 4) 最近会话失败（10 分钟内）→ 红快闪
        if let s = latest, s.outcome == "failed",
           now - s.updated < kFailureWindow * 1000 {
            return LightState(mode: .red, blink: .redFast,
                              headline: "任务失败",
                              detail: "最近一次会话以失败结束。\n会话：\(s.title)",
                              connected: true, running: 0, perms: 0, forms: 0)
        }

        // 5) 完成未查看（90 秒内）→ 绿柔闪
        if let s = latest, s.outcome == "succeeded",
           let idle = s.idle,
           now - idle < kDoneWindow * 1000 {
            let viewed = s.viewed ?? 0
            if viewed < idle {
                return LightState(mode: .green, blink: .greenSoft,
                                  headline: "任务完成",
                                  detail: "最近一次会话已完成，等你查看。\n会话：\(s.title)",
                                  connected: true, running: 0, perms: 0, forms: 0)
            }
        }

        // 6) 空闲 → 绿常亮
        if let s = latest {
            return LightState(mode: .green, blink: .none,
                              headline: "全部完成",
                              detail: "没有正在运行的任务。\n最近会话：\(s.title)",
                              connected: true, running: 0, perms: 0, forms: 0)
        }

        return LightState(mode: .green, blink: .none,
                          headline: "全部完成",
                          detail: "当前没有会话。",
                          connected: true, running: 0, perms: 0, forms: 0)
    }
}

// MARK: - 灯视图

final class TrafficLightView: NSView {

    private(set) var state = LightState()
    private weak var actions: AppActions?

    private var dragStartScreen: NSPoint?
    private var dragStartOrigin: NSPoint?

    private let housingLayer = CALayer()
    private var lampLayers: [CALayer] = []
    private var housingImage: CGImage?
    private var darkImage: CGImage?
    private var litImages: [CGImage?] = []
    private var scale: CGFloat = 2

    init(frame: NSRect, actions: AppActions) {
        self.actions = actions
        super.init(frame: frame)
        wantsLayer = true
        layer?.masksToBounds = false
        buildLayers()
    }

    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        let newScale = window?.backingScaleFactor
            ?? NSScreen.screens.first?.backingScaleFactor
            ?? 2
        if newScale != scale || housingImage == nil {
            recoverScale(newScale)
        }
    }

    private func recoverScale(_ newScale: CGFloat) {
        scale = newScale
        buildArtwork()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        housingLayer.contents = housingImage
        housingLayer.contentsScale = scale
        for lamp in lampLayers {
            lamp.contentsScale = scale
            lamp.contents = darkImage
        }
        CATransaction.commit()
        refreshLamps()
    }

    // MARK: 状态

    func apply(_ newState: LightState) {
        let lampChanged = newState.mode != state.mode || newState.blink != state.blink
        state = newState
        let detail = newState.detail.isEmpty ? "" : "\n" + newState.detail
        toolTip = newState.headline + detail
        if lampChanged { refreshLamps() }
    }

    private var litIndexAtTop: Int {
        switch state.mode {
        case .red: return 0
        case .yellow: return 1
        case .green: return 2
        }
    }

    // MARK: 图层

    private func lampRect(atTop index: Int) -> NSRect {
        lampRect(atTop: index, in: bounds.size)
    }

    private func lampRect(atTop index: Int, in size: NSSize) -> NSRect {
        let housing = NSRect(origin: .zero, size: size).insetBy(dx: 5, dy: 5)
        let total = kLampDiameter * 3 + kLampGap * 2
        let topPadding = (housing.height - total) / 2
        let y = housing.maxY - topPadding - kLampDiameter - CGFloat(index) * (kLampDiameter + kLampGap)
        return NSRect(x: housing.midX - kLampDiameter / 2, y: y,
                      width: kLampDiameter, height: kLampDiameter)
    }

    private func buildLayers() {
        housingLayer.frame = bounds
        housingLayer.contents = housingImage
        housingLayer.contentsScale = scale
        layer?.addSublayer(housingLayer)

        for index in 0..<3 {
            let lamp = CALayer()
            lamp.frame = lampRect(atTop: index).insetBy(dx: -5, dy: -5)
            lamp.contentsGravity = .resize
            lamp.contentsScale = scale
            lamp.contents = darkImage
            layer?.addSublayer(lamp)
            lampLayers.append(lamp)
        }
    }

    private func refreshLamps() {
        let lit = litIndexAtTop
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for (index, lamp) in lampLayers.enumerated() {
            lamp.removeAnimation(forKey: "blink")
            lamp.contents = (index == lit) ? litImages[index] : darkImage
            lamp.opacity = 1
        }
        CATransaction.commit()

        guard state.blink != .none, lampLayers.indices.contains(lit) else { return }
        addBlinkAnimation(to: lampLayers[lit])
    }

    private func addBlinkAnimation(to lamp: CALayer) {
        switch state.blink {
        case .none:
            return
        case .yellowPulse:
            let animation = CABasicAnimation(keyPath: "opacity")
            animation.fromValue = 0.34
            animation.toValue = 1.0
            animation.duration = 0.8            // 一呼一吸 = 1.6 秒
            animation.autoreverses = true
            animation.repeatCount = .infinity
            animation.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            lamp.add(animation, forKey: "blink")
        case .greenSoft:
            let animation = CABasicAnimation(keyPath: "opacity")
            animation.fromValue = 0.52
            animation.toValue = 1.0
            animation.duration = 0.5            // 一呼一吸 = 1.0 秒
            animation.autoreverses = true
            animation.repeatCount = .infinity
            animation.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            lamp.add(animation, forKey: "blink")
        case .redFast:
            let animation = CAKeyframeAnimation(keyPath: "opacity")
            animation.values = [1.0, 1.0, 0.2, 0.2]
            animation.keyTimes = [0, 0.59, 0.61, 1]
            animation.duration = 0.5            // 2 Hz
            animation.repeatCount = .infinity
            animation.calculationMode = .linear
            lamp.add(animation, forKey: "blink")
        }
    }

    // MARK: 绘制素材

    private func render(size: NSSize, _ draw: () -> Void) -> CGImage? {
        guard size.width > 0, size.height > 0 else { return nil }
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil,
                                         pixelsWide: Int(size.width * scale),
                                         pixelsHigh: Int(size.height * scale),
                                         bitsPerSample: 8,
                                         samplesPerPixel: 4,
                                         hasAlpha: true,
                                         isPlanar: false,
                                         colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0,
                                         bitsPerPixel: 0) else { return nil }
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        if let context = NSGraphicsContext(bitmapImageRep: rep) {
            NSGraphicsContext.current = context
            context.cgContext.setShouldAntialias(true)
            draw()
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage
    }

    private func buildArtwork() {
        housingImage = render(size: bounds.size) { [weak self] in
            self?.drawHousing()
        }

        let padding: CGFloat = 5
        let lampSize = NSSize(width: kLampDiameter + padding * 2,
                              height: kLampDiameter + padding * 2)
        let lampRectInImage = NSRect(x: padding, y: padding,
                                     width: kLampDiameter, height: kLampDiameter)

        darkImage = render(size: lampSize) { [weak self] in
            self?.drawLamp(in: lampRectInImage, color: .gray, lit: false)
        }

        litImages = (0..<3).map { index in
            render(size: lampSize) { [weak self] in
                guard let self else { return }
                self.drawLamp(in: lampRectInImage, color: self.lampColor(at: index), lit: true)
            }
        }
    }

    private func drawHousing() {
        drawHousing(in: bounds.size)
    }

    private func drawHousing(in size: NSSize) {
        let housing = NSRect(origin: .zero, size: size).insetBy(dx: 5, dy: 5)
        let path = NSBezierPath(roundedRect: housing,
                                xRadius: housing.width / 2,
                                yRadius: housing.width / 2)
        NSColor(srgbRed: 0.088, green: 0.094, blue: 0.101, alpha: 1).setFill()
        path.fill()
        NSColor(white: 1, alpha: 0.065).setStroke()
        path.lineWidth = 0.8
        path.stroke()
    }

    private func lampColor(at index: Int) -> NSColor {
        switch index {
        case 0: return NSColor(srgbRed: 0.92, green: 0.29, blue: 0.24, alpha: 1)
        case 1: return NSColor(srgbRed: 0.98, green: 0.76, blue: 0.29, alpha: 1)
        default: return NSColor(srgbRed: 0.25, green: 0.80, blue: 0.42, alpha: 1)
        }
    }

    private func drawLamp(in rect: NSRect, color base: NSColor, lit: Bool, opacity: CGFloat = 1) {
        if !lit {
            NSColor(srgbRed: 0.118, green: 0.125, blue: 0.133, alpha: 1).setFill()
            NSBezierPath(ovalIn: rect).fill()
            NSColor(white: 1, alpha: 0.04).setStroke()
            let inner = NSBezierPath(ovalIn: rect.insetBy(dx: 0.5, dy: 0.5))
            inner.lineWidth = 1
            inner.stroke()
            return
        }

        if opacity <= 0.01 { return }

        // 外发光
        base.withAlphaComponent(0.20 * opacity).setFill()
        NSBezierPath(ovalIn: rect.insetBy(dx: -3, dy: -3)).fill()

        // 主体：径向渐变，中心偏上
        let hot = (base.blended(withFraction: 0.42, of: .white) ?? base).withAlphaComponent(opacity)
        let mid = base.withAlphaComponent(opacity)
        let edge = (base.blended(withFraction: 0.30, of: .black) ?? base).withAlphaComponent(opacity)
        if let gradient = NSGradient(colors: [hot, mid, edge]) {
            gradient.draw(in: NSBezierPath(ovalIn: rect),
                          relativeCenterPosition: NSPoint(x: -0.12, y: 0.28))
        }

        // 高光（小而柔）
        let highlightRect = rect.insetBy(dx: rect.width * 0.34, dy: rect.height * 0.36)
            .offsetBy(dx: -rect.width * 0.02, dy: rect.height * 0.12)
        NSColor(white: 1, alpha: 0.16 * opacity).setFill()
        NSBezierPath(ovalIn: highlightRect).fill()

        // 边缘压暗
        base.blended(withFraction: 0.32, of: .black)?.withAlphaComponent(0.5 * opacity).setStroke()
        let rim = NSBezierPath(ovalIn: rect.insetBy(dx: 0.5, dy: 0.5))
        rim.lineWidth = 1
        rim.stroke()
    }

    // MARK: 导出（说明书配图）

    /// 把指定状态渲染成透明背景 PNG，供文档使用。
    func pngData(for state: LightState, scale s: CGFloat = 3, opacity: CGFloat = 1) -> Data? {
        let size = NSSize(width: kWindowWidth, height: kWindowHeight)
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil,
                                         pixelsWide: Int(size.width * s),
                                         pixelsHigh: Int(size.height * s),
                                         bitsPerSample: 8,
                                         samplesPerPixel: 4,
                                         hasAlpha: true,
                                         isPlanar: false,
                                         colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0,
                                         bitsPerPixel: 0) else { return nil }
        rep.size = size

        NSGraphicsContext.saveGraphicsState()
        if let context = NSGraphicsContext(bitmapImageRep: rep) {
            NSGraphicsContext.current = context
            context.cgContext.setShouldAntialias(true)

            drawHousing(in: size)

            let litIndex: Int
            switch state.mode {
            case .red: litIndex = 0
            case .yellow: litIndex = 1
            case .green: litIndex = 2
            }

            for index in 0..<3 {
                let rect = lampRect(atTop: index, in: size)
                drawLamp(in: rect, color: lampColor(at: index), lit: index == litIndex,
                         opacity: index == litIndex ? opacity : 1)
            }
        }
        NSGraphicsContext.restoreGraphicsState()

        return rep.representation(using: .png, properties: [:])
    }

    // MARK: 交互

    override func mouseDown(with event: NSEvent) {
        dragStartScreen = NSEvent.mouseLocation
        dragStartOrigin = window?.frame.origin
    }

    override func mouseDragged(with event: NSEvent) {
        guard let startScreen = dragStartScreen, let startOrigin = dragStartOrigin,
              let window else { return }
        let now = NSEvent.mouseLocation
        let dx = now.x - startScreen.x
        let dy = now.y - startScreen.y
        window.setFrameOrigin(NSPoint(x: startOrigin.x + dx, y: startOrigin.y + dy))
    }

    override func mouseUp(with event: NSEvent) {
        if dragStartScreen != nil {
            actions?.saveWindowFrame()
        }
        dragStartScreen = nil
        dragStartOrigin = nil
    }

    override func rightMouseDown(with event: NSEvent) {
        guard let menu = actions?.buildMenu() else { return }
        NSMenu.popUpContextMenu(menu, with: event, for: self)
    }
}

// MARK: - 动作协议

protocol AppActions: AnyObject {
    func saveWindowFrame()
    func buildMenu() -> NSMenu
}

// MARK: - AppDelegate

final class AppDelegate: NSObject, NSApplicationDelegate, AppActions {

    private var window: NSWindow!
    private var view: TrafficLightView!
    private var monitorTimer: Timer?
    private var pollBusy = false
    private var selfTestIndex = 0
    private var selfTestTimer: Timer?
    private let isSelfTest = CommandLine.arguments.contains("--selftest")

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        let size = NSSize(width: kWindowWidth, height: kWindowHeight)
        let origin = savedOrigin(for: size)
        window = NSWindow(contentRect: NSRect(origin: origin, size: size),
                          styleMask: [.borderless],
                          backing: .buffered,
                          defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = true
        window.level = .floating
        window.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]

        view = TrafficLightView(frame: NSRect(origin: .zero, size: size), actions: self)
        window.contentView = view
        window.orderFrontRegardless()

        if let arg = CommandLine.arguments.first(where: { $0.hasPrefix("--export=") }) {
            let directory = String(arg.dropFirst("--export=".count))
            exportImages(to: directory)
            NSApp.terminate(nil)
            return
        }

        if isSelfTest {
            startSelfTest()
        } else {
            startPolling()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        monitorTimer?.invalidate()
        selfTestTimer?.invalidate()
    }

    // MARK: 轮询

    private func startPolling() {
        let timer = Timer(timeInterval: kPollInterval, repeats: true) { [weak self] _ in
            self?.poll()
        }
        RunLoop.main.add(timer, forMode: .common)
        monitorTimer = timer
        poll()
    }

    private func poll() {
        guard !pollBusy else { return }
        pollBusy = true
        DispatchQueue.global(qos: .utility).async {
            let state = Monitor.snapshot()
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.pollBusy = false
                self.view.apply(state)
            }
        }
    }

    // MARK: 自检

    private func startSelfTest() {
        let states: [LightState] = [
            LightState(mode: .yellow, blink: .none, headline: "任务进行中", detail: "自检：黄灯常亮", connected: true, running: 1),
            LightState(mode: .yellow, blink: .yellowPulse, headline: "等待你确认", detail: "自检：黄灯呼吸", connected: true, forms: 1),
            LightState(mode: .red, blink: .none, headline: "需要你处理", detail: "自检：红灯常亮", connected: true, perms: 1),
            LightState(mode: .red, blink: .redFast, headline: "任务失败", detail: "自检：红灯快闪", connected: true),
            LightState(mode: .green, blink: .greenSoft, headline: "任务完成", detail: "自检：绿灯柔闪", connected: true),
            LightState(mode: .green, blink: .none, headline: "全部完成", detail: "自检：绿灯常亮", connected: true)
        ]

        // --state=yellow|pulse|red|redfast|green|greensoft：固定显示单个状态，便于截图/调试
        if let arg = CommandLine.arguments.first(where: { $0.hasPrefix("--state=") }) {
            let name = String(arg.dropFirst("--state=".count)).lowercased()
            let map: [String: Int] = ["yellow": 0, "pulse": 1, "red": 2, "redfast": 3,
                                      "green": 5, "greensoft": 4, "done": 4]
            let index = map[name] ?? 0
            applySelfTest(states[index])
            return
        }

        applySelfTest(states[0])
        let timer = Timer(timeInterval: 3.0, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.selfTestIndex = (self.selfTestIndex + 1) % states.count
            self.applySelfTest(states[self.selfTestIndex])
        }
        RunLoop.main.add(timer, forMode: .common)
        selfTestTimer = timer
    }

    private func applySelfTest(_ state: LightState) {
        let line = "\(Date().timeIntervalSince1970) \(state.headline) \(state.detail)\n"
        FileHandle.standardOutput.write(Data(line.utf8))
        view.apply(state)
    }

    // MARK: 导出

    private func exportImages(to directory: String) {
        try? FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)

        let states: [(String, LightState)] = [
            ("green", LightState(mode: .green, blink: .none, headline: "全部完成", detail: "")),
            ("yellow", LightState(mode: .yellow, blink: .none, headline: "任务进行中", detail: "")),
            ("red", LightState(mode: .red, blink: .none, headline: "需要你处理", detail: "")),
        ]

        for (name, state) in states {
            guard let data = view.pngData(for: state) else { continue }
            let url = URL(fileURLWithPath: directory).appendingPathComponent("\(name).png")
            try? data.write(to: url)
        }

        // 闪烁状态导出亮/暗两帧：静态图无法表达周期，文档里并排说明
        let blinking: [(String, LightState, CGFloat, CGFloat)] = [
            ("greensoft", LightState(mode: .green, blink: .greenSoft, headline: "任务完成", detail: ""), 1.0, 0.52),
            ("pulse", LightState(mode: .yellow, blink: .yellowPulse, headline: "等待你确认", detail: ""), 1.0, 0.34),
            ("redfast", LightState(mode: .red, blink: .redFast, headline: "任务失败", detail: ""), 1.0, 0.18),
        ]

        for (name, state, bright, dim) in blinking {
            if let data = view.pngData(for: state, opacity: bright) {
                let url = URL(fileURLWithPath: directory).appendingPathComponent("\(name)-bright.png")
                try? data.write(to: url)
            }
            if let data = view.pngData(for: state, opacity: dim) {
                let url = URL(fileURLWithPath: directory).appendingPathComponent("\(name)-dim.png")
                try? data.write(to: url)
            }
        }
    }

    // MARK: 位置

    private func savedOrigin(for size: NSSize) -> NSPoint {
        let defaults = UserDefaults.standard
        if let saved = defaults.string(forKey: "TrafficLightWindowFrame") {
            let rect = NSRectFromString(saved)
            if rect.width > 1 && rect.height > 1,
               NSScreen.screens.contains(where: { $0.visibleFrame.intersects(rect) }) {
                return NSPoint(x: rect.origin.x, y: rect.origin.y)
            }
        }
        return defaultOrigin(for: size)
    }

    private func defaultOrigin(for size: NSSize) -> NSPoint {
        let screen = NSScreen.screens.first
            ?? NSScreen.main
            ?? NSScreen.screens.first
        let frame = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        return NSPoint(x: frame.maxX - size.width - 16,
                       y: frame.maxY - size.height - 16)
    }

    func saveWindowFrame() {
        guard let window else { return }
        UserDefaults.standard.set(NSStringFromRect(window.frame), forKey: "TrafficLightWindowFrame")
    }

    // MARK: 菜单

    func buildMenu() -> NSMenu {
        let menu = NSMenu()

        let refresh = NSMenuItem(title: "立即刷新", action: #selector(menuRefresh), keyEquivalent: "")
        refresh.target = self
        menu.addItem(refresh)

        let open = NSMenuItem(title: "打开 OpenCode", action: #selector(menuOpen), keyEquivalent: "")
        open.target = self
        menu.addItem(open)

        menu.addItem(.separator())

        let pin = NSMenuItem(title: "始终置顶", action: #selector(menuTogglePin), keyEquivalent: "")
        pin.target = self
        pin.state = window.level == .floating ? .on : .off
        menu.addItem(pin)

        let reset = NSMenuItem(title: "复位位置", action: #selector(menuResetPosition), keyEquivalent: "")
        reset.target = self
        menu.addItem(reset)

        menu.addItem(.separator())

        let quit = NSMenuItem(title: "退出", action: #selector(menuQuit), keyEquivalent: "")
        quit.target = self
        menu.addItem(quit)

        return menu
    }

    @objc private func menuRefresh() {
        poll()
    }

    @objc private func menuOpen() {
        let url = URL(fileURLWithPath: "/Applications/OpenCode.app")
        if FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
        } else {
            NSWorkspace.shared.open(URL(string: "https://opencode.ai")!)
        }
    }

    @objc private func menuTogglePin() {
        window.level = (window.level == .floating) ? .normal : .floating
    }

    @objc private func menuResetPosition() {
        let size = window.frame.size
        window.setFrameOrigin(defaultOrigin(for: size))
        saveWindowFrame()
    }

    @objc private func menuQuit() {
        // 让 watcher 在本次 OpenCode 运行期间不再拉起
        let flag = ("~/Library/Application Support/OpenCodeTrafficLight/quit-flag" as NSString).expandingTildeInPath
        FileManager.default.createFile(atPath: flag, contents: Data())
        NSApp.terminate(nil)
    }
}

// MARK: - 入口

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
