# CanKit – Review des `develop`-Branches

**Datum:** 2026-07-18 · **Stand:** `develop` @ `4fff5c8` (Diff gegen `main` @ `e3f08c1`: 245 Dateien, +27.566 / −3.725 Zeilen) · **Vorgänger-Review:** [2026-07-14-deep-code-review.md](2026-07-14-deep-code-review.md)

**Umfang dieses Reviews:** Statische Durchsicht der seit dem letzten Review neu entstandenen Pro-Schichten (Actor, RawCan, Reliability, Addressing, IsoTp, J1939Tp, Uds, J1939, CANopen, Hawe), der Core-/Adapter-Änderungen sowie Follow-up-Prüfung aller Befunde aus dem Review vom 14.07. Kein Build/Testlauf in dieser Umgebung (kein dotnet-SDK verfügbar); die CI-Historie der gemergten PRs (#17–#43) war durchgängig grün.

---

## Gesamteinschätzung

Der `develop`-Branch ist ein großer Qualitätssprung. Die im letzten Review als „funktional defekt“ eingestufte ISO-TP-Schicht wurde komplett entfernt und durch eine saubere, geschichtete Pro-Architektur ersetzt (L2 `CanBusService`/`ProtocolActor`/`DeadlineScheduler` → L3 `IsoTp`/`J1939Tp` → L4 `Uds`/`J1939`/`CANopen`/`Hawe`). Die neuen Protokollstacks teilen ein konsistentes Muster: Single-Writer-Actor für allen Protokollzustand, ereignisgetriebene Deadlines statt Busy-Loops, Bounded-Inbox mit Fault-Items (blockierte `ReceiveAsync`-Aufrufer erhalten Abbrüche als Exception statt ewig zu hängen) und TX-Confirm über Echo-Matching.

Auffällig positiv: Die Codebasis dokumentiert Race-Fixes inline mit Referenz auf die auslösenden Bugbot-/Review-Findings, jedes neue Paket hat eigene slnf-Filter, CI-Workflows und umfangreiche Virtual-Loopback-Integrationstests (u. a. ~1.300 Zeilen IsoTp-, ~1.050 Zeilen J1939Tp-, ~1.150 Zeilen UDS-, ~1.400 Zeilen CANopen-Tests). Von den 30+ Befunden des Vorgänger-Reviews sind fast alle nachweislich behoben (Abschnitt 1).

Es wurden **keine kritischen Defekte** gefunden. Die verbleibenden Punkte (Abschnitt 3) sind Konsistenz-, Robustheits- und Dokumentationsthemen.

---

## 1. Follow-up: Befunde aus dem Review vom 2026-07-14

| Alt-Befund | Status auf `develop` |
|---|---|
| §1.1 ISO-TP-Transport funktional defekt (16 Einzelfehler) | **Behoben durch Ersatz.** `CanKit.Transport.IsoTp` inkl. `Excpetions`-Namespace und PEAK-Fehlreferenz vollständig entfernt; Neuimplementierung `CanKit.Pro.IsoTp` adressiert jedes Einzeldefekt explizit (invertiertes `canfd` → Codec frame-kind-agnostisch; FC-PCI-Nibble korrekt; Padding nach BS/STmin; FF-Längen-Klammerung korrekt; `EncodeStMin(0/1 ms)` gültig; reservierte STmin-Werte → 127 ms; SN startet bei 1; ereignisgetriebener Actor statt Busy-Scheduler; N_As/N_Bs/N_Cr werden real geprüft) |
| §1.2 `QueuedTxCanBus`: Batch-Reste hängen | **Behoben** (`WaitToReadAsync` nur bei `index == 0`, `QueuedTxCanBus.cs:202`) |
| §1.3 Stopwatch nie gestartet (SocketCAN/ZLG) | **Behoben** (kein `new Stopwatch()`-ohne-Start-Muster mehr vorhanden) |
| §1.4 `BCMPeriodicTx.Update` FD-Zweig / `RemainingCount` EAGAIN | **Behoben** (FD-Zweig korrekt, `BCMPeriodicTx.cs:237`; `RemainingCount` mit poll-Retry und Fallback auf letzten bekannten Wert, `:259` ff.); zusätzlich ctor-FD-Leak und TX-Lease-Kopie gefixt (PR #42) |
| §1.5 `CanFrame.Dispose()` ignoriert `ownMemory` | **Behoben** (`if (OwnMemory) _memoryOwner?.Dispose();`, `CanFrame.cs:425`) |
| §2.1 Frame-Ownership uneinheitlich | **Behoben:** Ownership-Vertrag in arc42 §8.1 dokumentiert; `VirtualBusHub.Broadcast` dupliziert pro Empfänger (`Duplicate(bus.Options.BufferAllocator)`); Pro-Schichten kopieren RX-Payloads defensiv (`frame.Data.ToArray()`), bevor sie den Subscription-Puffer verlassen |
| §2.2 `AsyncFramePipe` (verwaiste Reader, verschluckte Cancellation) | **Behoben:** Exception-Pulse-Verlierer wird per `waitCts` abgeräumt, Caller-Cancel gewinnt gegen Hintergrund-Fault, Kontrakt (Timeout → Teilliste, Cancel → OCE) ist jetzt dokumentiert und getestet |
| §2.3 `SoftwarePeriodicTx` (macOS-Busy-Loop, `Completed` im Lock) | **Behoben:** eigener macOS-Pfad (`Sleep_*`-Delegates); `Completed`/`Stop` außerhalb `_gate` mit Re-Check gegen konkurrierendes `Update`; neu: `IPeriodicTx.Faulted`-Event, Transmit-Exceptions werden nicht mehr verschluckt, Loop bleibt am Leben |
| §2.4 `VirtualBusHub._hubs`-Leak, Copy-Paste-Logs | **Behoben** (Hub-Entfernung beim letzten Detach unter `_hubsGate`; Log-Strings in Virtual/SocketCAN korrigiert, `b54575b`) |
| §2.5 `CanEndpoint.Parse` lowercased Host | **Behoben:** manueller Tokenizer statt `System.Uri`, Host-Case bleibt erhalten, Sonderzeichen/Query-only-Endpoints definiert, 221 Zeilen Parse-Tests |
| §2.5 `BitTimingSolver.Clamp`-Crash | **Behoben** (`maxTseg1 < Tseg1Min → continue`, `BitTimingSolver.cs:45`) |
| §3 Typos in öffentlicher API (`ReadTImeOutMs`, `ExceptionOccured`, `Excpetions`) | **Behoben** (NFR-011-Sweep, als Breaking Changes im CHANGELOG dokumentiert) |
| §2.5 `SystemTimestamp = DateTime.Now` | **Behoben** für die angefassten Pfade (NFR-012: Default `UtcNow`, Diagnostics-Hot-Paths, Test `SystemTimestampUtcTests`) |
| §2.4 ZLG-Transceiver: Teil-Erfolg beim Zwischen-Flush | **Offen** – siehe Befund 3.4 |
| §2.5 `CanBus.Open<TBus,…>(DeviceType)` Device-Leak bei Open-Throw | **Teilweise** – Diff zeigt Umbauten in `CanBus.cs`; der Fehlerpfad „CreateDevice ok, Open wirft“ sollte gezielt nachgeprüft/getestet werden |

---

## 2. Stärken der neuen Architektur

- **Einheitliches L2-Fundament:** `CanBusService` (Copy-on-Write-Subscription-Snapshot, lock-freier Dispatch-Hot-Path, FIFO-Echo-Matching für `SendConfirmed` bei byte-identischen Frames), `ProtocolActor` (inkl. `IsOnCurrentActor` für reentrante Sync-Reads) und `DeadlineScheduler` (CAS-State-Machine `Pending → Expired|Completed|Cancelled`, Generation-Counter gegen stale Timer-Fires) sind sauber dokumentiert und werden von allen vier Protokollstacks identisch benutzt.
- **RX-Fehlerpfade sind vollständig:** Alle Kanäle liefern Reassembly-Abbrüche (SN-Mismatch, Timer-Ablauf, Peer-Abort, Supersede) sowohl über `BackgroundExceptionOccurred` als auch als Fault-Item in die Empfänger-Inbox – kein hängender `ReceiveAsync` mehr.
- **Abwehr gegen bösartige Peers:** ISO-TP `TryParsePci` ist vollständig bounds-checked und lehnt Escape-Header auf Classic-CAN ab; J1939Tp validiert RTS/BAM-Totals (`TotalPackets(totalBytes) == totalPackets`), maskiert TP.CM-PGN-Reserved-Bits und begrenzt CTS-Anforderungen; CANopen deckelt SDO-Allokationen über `MaxSdoTransferBytes` (Default 1 MiB) und beantwortet Über-Cap mit SDO-Abort 0x05040005.
- **UDS-Client protokollfest:** Request-Lock über komplette Mehrschritt-Sequenzen (SecurityAccess Seed+Key, `DownloadAsync` 0x34→0x36…→0x37), P2/P2*-Budget mit NRC-0x78-Zähler, `DiscardPendingPdus` vor jedem Send gegen Late-Replies, BSC-Wrap 0xFF→0x00 korrekt.
- **J1939 Address-Claim:** BeginClaim invalidiert die Adresse vor der Announce, Arbitrierungsfenster startet erst nach TX-Confirm, Verlierer-Pfad (Cannot-Claim + TP-Rebind auf 0xFE) und die Claim/Send-Race-Gates (`HasReclaimCrossed`) sind konsequent; eigener Echo-Filter über NAME-Gleichheit.
- **Tests/CI:** 19 pfadgefilterte Workflows (je Adapter + je Pro-Paket), Virtual-Loopback-Integrationstests für jeden Stack, dedizierte Regressionstests für die Race-Fixes (`CoreResidualRegressionTests`, `SoftwarePeriodicTxFaultedTests`, `SocketCanBcmOwnershipTests`, `PeriodicJitterTests` mit NFR-001-Soft-Gate).

---

## 3. Befunde

### 3.1 J1939Tp/J1939/CANopen: Events werden synchron auf dem Actor-Loop gefeuert (Konsistenz zu IsoTp)

`IsoTpChannel.EmitPdu` hebt `DatagramReceived` bewusst per `Task.Run` **vom Actor-Loop herunter**, damit ein Handler, der synchron auf `ReceiveAsync`/`SendAsync`/`DiscardPendingPdus` wartet, die Mailbox nicht deadlockt (dort als Bugbot 3596580061 dokumentiert). `J1939TpChannel.EmitPdu` (`J1939TpChannel.cs:936-947`) und `J1939NodeImpl.EmitMessage` (`J1939NodeImpl.cs:882-893`) rufen ihre Events dagegen **synchron auf dem Actor-Loop** auf. Ein blockierender `DatagramReceived`-/`MessageReceived`-Handler hält damit sämtliche T1/Tr/T2/T3-Timer und alle parallelen Sessions des Kanals an; ein Handler, der synchron auf denselben Kanal zurückwartet, deadlockt.

CANopen hat mit dem Bounded-Event-Pump (`_eventChannel` + `RunEventPumpAsync`) bereits die dritte – und robusteste – Lösung.

**Empfehlung:** Entweder das CANopen-Event-Pump-Muster in J1939Tp/J1939 übernehmen oder mindestens im XML-Doc von `IJ1939TpChannel.DatagramReceived`/`IJ1939Node.MessageReceived` festschreiben, dass Handler nicht blockieren dürfen (bei `IJ1939Node` ist die Actor-Affinität dokumentiert, das Blocking-Verbot aber nur implizit). *(mittel)*

### 3.2 IsoTp: kein konfigurierbares RX-Größenlimit auf CAN-FD-Kanälen

`IsoTpChannel.HandleRxFirstFrame` begrenzt Reassembly auf `MaxPduLength`; für CAN-FD ist das aber `int.MaxValue` (`IsoTpChannel.cs:92-95`). Ein Peer kann mit einem einzigen Escape-FF (`FF_DL` bis 2³²−1) wiederholt Allokationsversuche bis 2 GiB auslösen. Die `OutOfMemoryException` wird zwar gefangen und mit FC(OVFLW) beantwortet (`:859-870`), aber jeder Versuch erzeugt massiven GC-/LOH-Druck – auf Embedded-Zielen ein DoS-Vektor. CANopen hat für das analoge Problem `MaxSdoTransferBytes` (Default 1 MiB) bekommen; IsoTp fehlt das Pendant.

**Empfehlung:** `IsoTpChannelOptions.MaxReceivePduLength` (Default z. B. 1 MiB) einführen und in `HandleRxFirstFrame` vor der Allokation prüfen → FC(OVFLW) ohne Allokationsversuch. *(mittel)*

### 3.3 J1939: `RequestPgnAsync` koppelt Request-Priorität an `ClaimPriority`

`J1939NodeImpl.RequestPgnAsync` (`J1939NodeImpl.cs:637`) baut die Request-PGN-Message mit `_options.ClaimPriority`, der Kommentar darüber verweist auf „Priorität 6 laut SAE J1939-21 §5.3.2“. Beide Defaults sind 6, daher fällt es heute nicht auf – wer aber `ClaimPriority` ändert, ändert unbeabsichtigt auch die Priorität aller Request-PGN-Frames.

**Empfehlung:** Eigene Konstante bzw. `DefaultPriority` verwenden (oder Parameter anbieten). *(gering, trivial)*

### 3.4 ZLG-Transceiver: Teil-Erfolg beim Zwischen-Flush erzeugt Duplikate (Alt-Befund, weiterhin offen)

`ZlgCanClassicTransceiver.Transmit` (`ZlgCanClassicTransceiver.cs:32-36`, identisch in der zweiten Überladung sowie im Fd-/Merge-Transceiver): Wenn der Batch-Zwischen-Flush `re != BATCH_COUNT` liefert, wird `return sent;` ausgeführt – die `re` tatsächlich akzeptierten Frames fehlen im Rückgabewert. Aufrufer (z. B. `QueuedTxCanBus`) senden diese Frames erneut → Duplikate auf dem Bus.

**Empfehlung:** `return sent + (int)re;`. *(mittel, trivial)*

### 3.5 Periodik über `Task.Delay`: Drift bei J1939-PeriodicSchedule und UDS-Keep-Alive

`J1939NodeImpl.PeriodicSchedule.LoopAsync` (`J1939NodeImpl.cs:1042-1064`) und `UdsClientImpl.TesterPresentKeepAlive.LoopAsync` wartet jeweils `Task.Delay(period)` **nach** dem Send. Die effektive Periode ist damit `period + Sendedauer (+ Timer-Granularität)` und driftet kumulativ – für J1939-Broadcast-PGNs mit nominell 100 ms relevant, zumal die SW-Fallback-Schiene laut Revert-Notiz (PR #33) jetzt der einzige Pfad ist. Der Kommentar zu FR-J1939-007 verweist auf die „L2 actor / DeadlineScheduler“-Timing-Qualität, `PeriodicSchedule` benutzt den `DeadlineScheduler` aber gar nicht.

**Empfehlung:** Auf absolute Zielzeitpunkte anchorn (nächster Tick = Start + n·Periode, wie in `SoftwarePeriodicTx`) oder auf `DeadlineScheduler`/`_actor.Schedule` umstellen. Für TesterPresent ist der Drift unkritisch (Toleranz S3 ≫), für J1939-PGNs sollte es zumindest dokumentiert sein. *(gering–mittel)*

### 3.6 API-Kleinigkeiten

| Fundstelle | Punkt |
|---|---|
| `IsoTpChannel.ReceiveAsync` (`IsoTpChannel.cs:218`), `J1939TpChannel.ReceiveAsync` (`:229`) | Nach Dispose wird `InvalidOperationException` geworfen; `ObjectDisposedException` wäre konsistenter zum Rest der API |
| `J1939TpChannel.HandleRxTpCm` RTS-Pfad (`:416`) | `maxCts == 0` (ungültig laut §5.10.3.1) wird stillschweigend wie „kein Limit“ behandelt; ein Abort mit Grund 1 wäre strenger |
| `J1939NodeImpl.SendCoreAsync` (`:572`) | `_transport` wird von Thread-Pool-Threads ohne `Volatile.Read` gelesen (Schreibseite nur Actor-Loop). Referenz-Reads sind atomar und der Fallback (`SourceAddress != sa` → Wegwerf-Kanal) plus `HasReclaimCrossed` fangen Staleness ab – ein `Volatile.Read` würde die Absicht aber explizit machen |
| `UdsClientImpl.ExecuteCoreAsync` catch-all (`:784-790`) | `DiscardStalePdus` läuft auch bei `UdsNegativeResponseException` (kein Late-Reply-Szenario); harmlos, aber ein gezielterer Filter (OCE/Timeout) wäre klarer |
| `CanOpenNode` MVP-Reset (`CanOpenNode.cs:727-737`) | `ResetNode`/`ResetCommunication` setzen nur Zustand+Bootup, ohne Kommunikationsparameter (PDO-Konfiguration, Heartbeat-Consumer) zurückzusetzen – als MVP kommentiert, sollte aber in `ICanOpenNode`-Doku sichtbar sein |

### 3.7 Betriebsvoraussetzung Echo-Mode dokumentieren

Alle Pro-Stacks bestätigen TX über `CanBusService.SendConfirmed`, das auf dem TX-Echo im `FrameObserved`-Strom basiert (ADR-7, FR-RAW-030..034). Auf einem Bus/Adapter ohne aktivierten Echo-Work-Mode läuft damit jeder ISO-TP-/J1939-/UDS-/CANopen-Send in den Confirm-Timeout (Default 1 s) und schlägt fehl. In arc42 ist das Design beschrieben; ein deutlicher Hinweis in den Getting-Started-/README-Abschnitten der Pro-Pakete („Kanal im Echo-Mode öffnen, sonst scheitert SendConfirmed“) fehlt aber und dürfte die häufigste Anwender-Stolperfalle werden. *(gering, Doku)*

### 3.8 CHANGELOG-Hygiene

Unter `## Unreleased` existieren die Abschnitte `### Added` und `### Fixed` jeweils **doppelt** (Zeilen 5/58 bzw. 29/51 – Konfliktauflösungs-Artefakt trotz Commit `fd82d28`, der genau das beheben sollte). Außerdem behauptet der CANopen-MVP-Eintrag (Zeile 26 f.) weiterhin, SDO-Block-Transfer (FR-CO-004) und Node-Guarding (FR-CO-009) seien „documented open items“ – beide sind inzwischen implementiert und getestet (`CanOpenNode.SdoBlock.cs`, `CanOpenNode.NodeGuarding.cs`, `CanOpenBlockAndGuardingTests`). Vor dem nächsten Release-Schnitt konsolidieren. *(gering, Doku)*

---

## 4. Tests & CI

- Jedes neue Paket hat Unit- **und** Virtual-Loopback-Integrationstests inkl. Negativpfaden (Timeouts, Aborts, SN-Mismatch, Supersede, Cancel-Races). Die im Alt-Review bemängelten Lücken (ISO-TP ungetestet, `QueuedCanBus`, `AsyncFramePipe`-Fehlerpfade) sind geschlossen.
- `PeriodicJitterTests` implementiert das NFR-001-Soft-Gate (mean-relative Jitter-Bound für CI) – pragmatisch für shared runners; das harte Ziel bleibt in der SRS dokumentiert.
- NuGet-Pack/Publish-Pipeline ist bewusst deaktiviert (auskommentierte Trigger) – korrekt, solange die Pro-Pakete pre-release sind; vor Reaktivierung Befund 3.8 beheben.
- BCM-Ownership-Tests laufen isoliert auf `vcan2` (PR #42) – gutes Muster gegen Test-Interferenz.

---

## 5. Priorisierte Empfehlungen

1. ZLG-Transceiver-Teil-Erfolg fixen (`sent + re`, vier Stellen) – Alt-Befund, trivial, verhindert Bus-Duplikate. *(mittel, trivial)*
2. `IsoTpChannelOptions.MaxReceivePduLength` einführen (RX-Allokations-Cap analog `MaxSdoTransferBytes`). *(mittel)*
3. Event-Dispatch in `J1939TpChannel`/`J1939NodeImpl` vom Actor-Loop entkoppeln (CANopen-Event-Pump-Muster) oder Blocking-Verbot hart dokumentieren. *(mittel)*
4. `RequestPgnAsync` von `ClaimPriority` entkoppeln. *(gering, trivial)*
5. Echo-Mode-Voraussetzung für `SendConfirmed` in den Pro-Paket-READMEs/Getting-Started prominent dokumentieren. *(gering)*
6. CHANGELOG konsolidieren (doppelte Abschnitte, veraltete CANopen-Open-Items) – vor dem nächsten Release. *(gering)*
7. Periodik-Drift in `PeriodicSchedule`/Keep-Alive anchorn oder dokumentieren. *(gering)*
8. `CanBus.Open<TBus,…>(DeviceType)`-Fehlerpfad (Device-Dispose bei Open-Throw) mit einem gezielten Test absichern. *(gering)*
