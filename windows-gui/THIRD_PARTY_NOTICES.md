# Third-party components and services

Dire Wolf Station (the Windows GUI in `windows-gui/`) is distributed under the same
license as Dire Wolf (GPL-2.0-or-later, see `LICENSE`).

## Runtime

| Component | Use | License |
|---|---|---|
| .NET 10 runtime and WPF (bundled in the self-contained ZIP) | application framework | MIT |
| Dire Wolf (`direwolf.exe`, optional in the ZIP) | the packet modem / TNC that the GUI controls | GPL-2.0-or-later |

The application has no other runtime dependencies, no advertising and no telemetry.

## Optional network service: map tiles

Map tiles are **off by default**.  When enabled in Settings, tiles are downloaded from the
configured tile server (default `https://tile.openstreetmap.org/`), cached on disk under
`%LOCALAPPDATA%\DireWolfStation\tiles` for 14 days, requested at most two at a time with an
identifying User-Agent, and the attribution "© OpenStreetMap contributors" is shown on
the map.  Map data © OpenStreetMap contributors, available under the Open Database
License (https://www.openstreetmap.org/copyright).  Heavy use should use another tile
provider that permits it (OSM tile usage policy: https://operations.osmfoundation.org/policies/tiles/).

## Build and test only (not distributed)

| Component | License |
|---|---|
| xUnit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
