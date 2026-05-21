<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Custom Instructions

## Implementation Guides

Before implementing or modifying any of the components listed below, read the
corresponding guide in full. The guides are **mandatory** — they are the canonical
reference for architecture decisions, naming conventions, lifecycle contracts, testing
requirements, and the implementation checklist for each component type.

| Component | Guide | Apply when… |
|---|---|---|
| **Protocol dissector** | [`NetworkInspector.Protocols/PROTOCOL_GUIDE.md`](NetworkInspector.Protocols/PROTOCOL_GUIDE.md) | A new `IProtocol` implementation is created or an existing dissector is modified. |
| **Frame source reader** | [`NetworkInspector.Sources/SOURCE_GUIDE.md`](NetworkInspector.Sources/SOURCE_GUIDE.md) | A new frame source (`IFrameSource`, `IFrameStreamSource`) is created or an existing reader is modified. |
| **Exporter** | [`NetworkInspector.Exporters/EXPORTER_GUIDE.md`](NetworkInspector.Exporters/EXPORTER_GUIDE.md) | A new exporter (`IFrameListener` or `IPacketListener`) is created or an existing exporter is modified. |
| **FrameBuilder layer** | [`NetworkInspector.FrameBuilder/FRAMEBUILDER_GUIDE.md`](NetworkInspector.FrameBuilder/FRAMEBUILDER_GUIDE.md) | A new protocol layer struct is created or an existing layer is modified. |
