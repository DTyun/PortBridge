---
version: alpha
name: PortBridge
description: A Chinese Windows tray utility for local port forwarding.
colors:
  background: '#F3F6FA'
  ink: '#183047'
  muted: '#53677B'
  primary: '#245C9E'
  success: '#14734B'
  error: '#AE342F'
typography:
  sans:
    fontFamily: Microsoft YaHei UI
  mono:
    fontFamily: Consolas
rounded:
  DEFAULT: 0px
spacing:
  page: 26px
  gap: 12px
components:
  button: {}
  input: {}
  status: {}
---

# PortBridge design

## Overview
Chinese-language desktop tool requested by the user, for configuring one local relay on Windows 11. No previous UI or business specification exists. Signature: a visible left-to-right address route, like two sockets joined on a patch panel. Calm blue, white and slate; no decorative animation or dashboard clutter.

## Tokens and ownership
Program.cs Theme is the canonical runtime palette and font owner. This document mirrors those tokens. Native Windows chrome, scrollbars, numeric fields, focus rings and menus remain platform-owned. No web styles or browser popup geometry applies.

## Layout and typography
Single resizable form; 26 px outer padding, 12 px action spacing, two balanced address columns. Chinese controls use Microsoft YaHei UI 10 pt; IP addresses and ports use Consolas 12 pt. DPI scaling is enabled; log owns remaining vertical space and scrolling. Minimum window prevents action clipping.

## Components and behavior
Native WinForms inputs and buttons provide keyboard focus, disabled, pressed and hover states. Accessible names identify address and port fields. Primary action uses blue; errors have persistent red text and log entries. Status always uses text as well as color. Start and pause share one handler across tray and window. Pause closes active sessions and frees listening sockets. Settings are locked during forwarding. Startup is an explicit opt-in stored in the current user's Run key; auto-forward is a separate opt-in. Closing hides to tray; Exit releases sockets and tray resources. Logs retain only a bounded in-memory history and never record packet contents.

## Canonical UI map
Fields: Field/Port helpers. Checkboxes: Check. Actions: ActionButton. Feedback: Feedback/AppendLog. State changes: Toggle/SetRunning. Tray: native ContextMenuStrip. No tables, routes, authentication, network API forms or destructive data operations.

Connection testing uses an owned modal WinForms window with a native URL field, scrolling log, persistent result, retry and cancel controls. It starts automatically, disables concurrent runs, supports cancellation by closing sockets, and restores paused state after temporary relays. Green success requires an actual HTTP 2xx response through the configured relay, plus certificate validation for HTTPS. Failure and cancellation have distinct text. The URL remains editable between runs. No settings are saved by testing. The existing main form layout and palette are reused.

Configuration import/export reuses action buttons on a second action row. Native Windows file pickers own file navigation and overwrite confirmation. Import is disabled while relaying and applies only after complete validation and successful atomic persistence. Export works while running and captures the form configuration. Startup registry preference is machine-specific and excluded. Existing per-user configuration wins over the bundled example; the example seeds first run only. Native minimum height grows by 50 px to preserve log space.

v1.3 uses a native TextBox for comma-separated listening ports; the target remains a numeric field. The field label and accessible name describe comma separation. One shared Settings parser owns validation, legacy single-port compatibility, import/export and diagnostics. RelayGroup owns all-or-nothing startup, stop and aggregate counters. Diagnostics report each listening port and require every port to pass.

v1.4 establishes the beginner-first desktop shell: a quiet pale slate canvas, white bordered cards, cobalt step markers, and one blue primary action. The visible sequence is always “本机入口 → 目标服务 → 传输方式 → 启动并测试 → 日志”. Helper copy sits beside the control it explains. The route arrow is the signature element: it makes the relay direction legible before a user reads any technical term. The default window is deliberately taller so the two startup checkboxes and configuration actions retain their full hit area at 100% Windows scaling. The runtime owner remains Program.cs Theme; colors are Background #F5F7FB, Surface #FFFFFF, Line #DDE4EE, Ink #172B4D, Muted #64748B, Accent #3366CC, AccentSoft #EAF0FF, Good #087F5B, Error #C2413B.

## Verification
Build with build.ps1. tests exercise real loopback TCP/UDP, concurrent sessions, half-close, port conflicts, stop/restart and loop prevention. Native UI workflow is verified separately. There is no sibling screen, browser surface, or web accessibility test suite.
