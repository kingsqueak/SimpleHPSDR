// SPDX-License-Identifier: GPL-2.0-or-later
//
// dial-helper: gives a Ulanzi Dial (D100H, Bluetooth LE HID) to SimpleHPSDR.
//
// As shipped the dial types Ctrl+V/C/Y/Z, sends media keys and volume, and
// clicks the mouse (pressed and turned). SimpleHPSDR starts this helper; it
// reads the dial's raw reports and prints one line per control to stdout:
//
//   hello dial-helper 1
//   device on | device off
//   down <button> | up <button>      L1 L2 R1 R2 (side: left top, left rear,
//                                    right top, right rear)  T1 T2 T3 (top:
//                                    left, centre, right)
//   click                            the dial pressed and let go
//   turn cw | turn ccw               one detent
//   pushturn cw | pushturn ccw       one detent with the dial held down
//   warn <text>                      something that could not be set up
//
// and it keeps the dial's own actions away from the rest of the Mac:
//
//   - a per-device key mapping (hidutil's UserKeyMapping, set for the dial
//     only) turns its keys, media keys and volume into nothing;
//   - an event tap drops the mouse clicks the system attributes to the dial
//     (needs the Accessibility permission; without it they still click).
//
// Opening the dial exclusively (IOKit seize) does not do this, even as root:
// macOS 27 accepts the option and the system's own driver keeps delivering
// (tried 2026-11-17). So nothing here needs root.
//
// Both are undone when the helper ends, and it ends when its stdin closes
// (SimpleHPSDR quit or crashed) or on SIGINT/SIGTERM.
//
//   dial-helper [--keep-system]      --keep-system: read only, change nothing

import Foundation
import CoreGraphics
import IOKit
import IOKit.hid

@_silgen_name("CGEventCopyIOHIDEvent") func CGEventCopyIOHIDEvent(_ e: CGEvent) -> Unmanaged<AnyObject>?
@_silgen_name("IOHIDEventGetSenderID") func IOHIDEventGetSenderID(_ e: AnyObject) -> UInt64

let vendorID = 0xfff1, productID = 0x82
let keepSystem = CommandLine.arguments.contains("--keep-system")
setvbuf(stdout, nil, _IOLBF, 0)
signal(SIGPIPE, SIG_IGN)
var deviceOn = false

func send(_ line: String) { print(line) }

// ---- keeping the dial's own actions away from the Mac ----

/// Volume, mute, previous/play/next, the V C Y Z keys and left Ctrl, as
/// (usage page << 32 | usage). Mapped to a usage that means nothing.
let silenced: [UInt64] = [0x0C000000E9, 0x0C000000EA, 0x0C000000E2, 0x0C000000B6, 0x0C000000CD, 0x0C000000B5,
                          0x700000019, 0x700000006, 0x70000001C, 0x70000001D, 0x7000000E0]

func setKeyMapping(_ on: Bool) {
    guard !keepSystem else { return }
    let pairs = on ? silenced.map { "{\"HIDKeyboardModifierMappingSrc\":\($0),\"HIDKeyboardModifierMappingDst\":\(0xFF00000000 as UInt64)}" } : []
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/hidutil")
    p.arguments = ["property", "--matching", "{\"VendorID\":\(vendorID),\"ProductID\":\(productID)}",
                   "--set", "{\"UserKeyMapping\":[\(pairs.joined(separator: ","))]}"]
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    do { try p.run(); p.waitUntilExit() } catch { send("warn could not run hidutil") ; return }
    if p.terminationStatus != 0 { send("warn hidutil failed, the dial's keys still reach other programs") }
}

/// The dial's event services, as the sender of the clicks it causes.
var dialSenders = Set<UInt64>()
func findDialSenders() {
    var ids = Set<UInt64>()
    var it: io_iterator_t = 0
    if IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOHIDEventService"), &it) == KERN_SUCCESS {
        while case let s = IOIteratorNext(it), s != 0 {
            let v = IORegistryEntryCreateCFProperty(s, "VendorID" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? Int
            let p = IORegistryEntryCreateCFProperty(s, "ProductID" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? Int
            if v == vendorID && p == productID {
                var id: UInt64 = 0
                IORegistryEntryGetRegistryEntryID(s, &id)
                ids.insert(id)
            }
            IOObjectRelease(s)
        }
        IOObjectRelease(it)
    }
    dialSenders = ids
}

var clickTap: CFMachPort?
let onClick: CGEventTapCallBack = { _, type, event, _ in
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        if let tap = clickTap { CGEvent.tapEnable(tap: tap, enable: true) }
        return Unmanaged.passUnretained(event)
    }
    if let h = CGEventCopyIOHIDEvent(event), dialSenders.contains(IOHIDEventGetSenderID(h.takeRetainedValue())) {
        return nil      // the dial's click: gone
    }
    return Unmanaged.passUnretained(event)
}
func startClickTap() {
    guard !keepSystem, clickTap == nil else { return }
    let mask: CGEventMask = [CGEventType.leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp]
        .reduce(0) { $0 | (1 << CGEventMask($1.rawValue)) }
    guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                      eventsOfInterest: mask, callback: onClick, userInfo: nil) else {
        send("warn no Accessibility permission: pressing and turning the dial still clicks the mouse")
        return
    }
    clickTap = tap
    CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0), .commonModes)
    CGEvent.tapEnable(tap: tap, enable: true)
}

func quit() -> Never {
    setKeyMapping(false)
    exit(0)
}

// ---- decoding ----
let sideKeys: [UInt8: String] = [0x19: "L1", 0x06: "L2", 0x1c: "R1", 0x1d: "R2"]
let topKeys: [UInt16: String] = [0x00b6: "T1", 0x00cd: "T2", 0x00b5: "T3"]
var sideDown = Set<String>()
var topDown: String?

func handle(_ id: UInt32, _ b: UnsafeBufferPointer<UInt8>) {
    switch id {
    case 1 where b.count >= 9:            // keyboard: id, modifiers, reserved, six keys
        var now = Set<String>()
        for k in b[3..<9] { if let name = sideKeys[k] { now.insert(name) } }
        for name in now.subtracting(sideDown).sorted() { send("down \(name)") }
        for name in sideDown.subtracting(now).sorted() { send("up \(name)") }
        sideDown = now
    case 2 where b.count >= 3:            // consumer control: id, 16-bit usage
        let usage = UInt16(b[1]) | UInt16(b[2]) << 8
        if let name = topKeys[usage] {
            if topDown != name {
                if let was = topDown { send("up \(was)") }
                topDown = name
                send("down \(name)")
            }
        } else {
            if let was = topDown { topDown = nil; send("up \(was)") }
            switch usage {
            case 0x00e2: send("click")
            case 0x00e9: send("turn cw")
            case 0x00ea: send("turn ccw")
            default: break
            }
        }
    case 3 where b.count >= 2:            // mouse: id, buttons, ...
        if b[1] & 0x02 != 0 { send("pushturn cw") }
        else if b[1] & 0x01 != 0 { send("pushturn ccw") }
    default: break                         // the vendor reports repeat the above
    }
}

// ---- the device ----
let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
IOHIDManagerSetDeviceMatching(manager, [kIOHIDVendorIDKey: vendorID, kIOHIDProductIDKey: productID] as CFDictionary)
var buffers: [IOHIDDevice: UnsafeMutablePointer<UInt8>] = [:]

let onReport: IOHIDReportCallback = { _, _, _, _, id, report, length in
    handle(id, UnsafeBufferPointer(start: report, count: length))
}
let onMatch: IOHIDDeviceCallback = { _, _, _, device in
    let r = IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone))
    guard r == kIOReturnSuccess else {
        send(String(format: "warn cannot open the dial: 0x%08x (Input Monitoring permission?)", r))
        return
    }
    let buf = UnsafeMutablePointer<UInt8>.allocate(capacity: 128)
    buffers[device] = buf
    IOHIDDeviceRegisterInputReportCallback(device, buf, 128, onReport, nil)
    deviceOn = true
    sideDown = []; topDown = nil
    // The mapping belongs to this connection of the dial: set it each time.
    setKeyMapping(true)
    findDialSenders()
    startClickTap()
    send("device on")
}
let onRemove: IOHIDDeviceCallback = { _, _, _, device in
    guard let buf = buffers.removeValue(forKey: device) else { return }
    buf.deallocate()
    deviceOn = !buffers.isEmpty
    if !deviceOn { dialSenders = []; send("device off") }
}
IOHIDManagerRegisterDeviceMatchingCallback(manager, onMatch, nil)
IOHIDManagerRegisterDeviceRemovalCallback(manager, onRemove, nil)
IOHIDManagerScheduleWithRunLoop(manager, CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue)
IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
send("hello dial-helper 1")

var sources: [DispatchSourceSignal] = []
for sig in [SIGINT, SIGTERM] {
    signal(sig, SIG_IGN)
    let s = DispatchSource.makeSignalSource(signal: sig, queue: .main)
    s.setEventHandler { quit() }
    s.resume()
    sources.append(s)
}
// SimpleHPSDR holds our stdin: when it goes, so do we.
let stdinSource = DispatchSource.makeReadSource(fileDescriptor: 0, queue: .main)
stdinSource.setEventHandler {
    var b = [UInt8](repeating: 0, count: 64)
    if read(0, &b, b.count) <= 0 { quit() }
}
stdinSource.resume()
CFRunLoopRun()
