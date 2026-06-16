# SmartLockerKiosk Invention Disclosure Packet

Prepared for review by a patent attorney or registered patent agent.

This document is a technical invention disclosure, not legal advice. It is intended to help counsel evaluate patentability, draft claims, and prepare a provisional or nonprovisional patent application.

## 1. Working Title

Automated Commissioning and Deterministic Physical Governance Platform for Electronic Locker Systems

Alternative titles:

- Automated Locker Topology Mapping and Commissioning System
- Guided Commissioning System for Electronic Locker Compartments
- Deterministic Physical Governance Platform for Commissioned Electronic Locker Infrastructure

## 2. Short Summary

SmartLockerKiosk includes a guided commissioning system that creates a trusted physical topology map for an electronic locker installation. The system detects controller communication ports, allows assignment of detected ports to left/right locker branches, scans daisy-chained relay boards by issuing open commands, detects which physical locker doors respond, visually marks detected ports, assigns physical locker numbers according to the sequence in which an installer closes doors, correlates each assigned physical locker to a branch and relay identifier, derives board/port information from the relay identifier, infers compartment size in supported installations, persists the resulting topology map, invalidates the commissioning code, and launches the operational kiosk application.

The same commissioned topology map supports runtime locker assignment, proximity-aware locker selection, deterministic authorization, physical access control, transaction journaling, backend acknowledgement, audit logging, and reconciliation.

## 3. Background Problem

Existing electronic locker commissioning processes typically require an installer or administrator to manually provide:

- a physical locker layout;
- a mapping between locker numbers and relay boards;
- a mapping between relay boards, ports, and physical doors;
- size or compartment metadata;
- backend configuration data; and
- confirmation that the physical wiring matches the entered configuration.

This manual process is slow, error-prone, and often requires one technician at the locker installation site communicating with another technician operating backend administration tools. The problem becomes more difficult when locker banks share control hardware, when wiring order does not match the desired physical numbering sequence, or when multiple relay boards are daisy-chained in each branch of the locker system.

SmartLockerKiosk addresses these problems by using the kiosk itself as a guided commissioning endpoint controlled by backend authorization and local physical feedback.

## 4. System Overview

The locker installation includes:

- a kiosk or controller-side computer running the SmartLockerKiosk application;
- one or two physical branches of lockers, typically left and right of the controller;
- one or more relay boards daisy-chained in each branch;
- electronically controlled locker doors connected to relay board ports;
- a local database storing commissioned topology and locker state;
- a backend service that validates commissioning, provides configuration, authorizes locker actions, receives acknowledgements, and supports auditability.

Relevant implementation artifacts in the current app include:

- `UI/CommissioningAccessWindow.xaml.vb`: commissioning entry, code validation, network validation, backend registration, health registration, and finalization.
- `UI/ControllerSetupWindow.xaml.vb`: USB/COM port detection and assignment to Branch A / Branch B.
- `UI/LockerCommissioningWindow.xaml.vb`: relay scanning, attached-door detection, closure-sequence assignment, size inference, and persistence.
- `Services/LockerController.vb`: branch connection, relay unlock, relay-to-board/port calculation, and locker-number unlock path.
- `Data/Locker.vb`: persisted locker topology fields including `Branch`, `RelayId`, `LockerNumber`, and `SizeCode`.
- `Models/LockerTransactionJournal.vb`: local transaction state, ACK status, transaction IDs, command IDs, actor, locker, branch, relay, and reconciliation state.
- `Audit/AuditModels.vb` and `Audit/auditLogger.vb`: audit event model and append-style JSONL logging.
- `Models/LockerAuthorizeRequestDto.vb`, `Models/LockerAuthorizeResponseDto.vb`, and `Models/LockerAckRequestDto.vb`: backend authorization and acknowledgement data structures.

## 5. Commissioning Workflow

In a representative embodiment, the commissioning process proceeds as follows.

1. The installer provides power to the locker system.
2. The kiosk boots to a commissioning screen.
3. The installer checks or validates network connectivity.
4. The installer enters a commissioning code.
5. The kiosk connects to the backend and validates the commissioning code.
6. The kiosk downloads site-specific and kiosk-specific configuration, including style/branding, workflow, location, kiosk identifier, runtime settings, and commissioning policy.
7. The software auto-detects USB/COM ports connected to the locker controller hardware.
8. The installer assigns detected communication ports to physical branches of the system, such as a left branch and a right branch. A system may have one branch or two branches, and each branch may include multiple daisy-chained relay boards.
9. The installer enters the locker commissioning screen and presses a control to connect to the relay boards.
10. The installer presses an "Open All" control.
11. The system scans relay positions on each branch. The number of ports to scan may be manually reduced to shorten scan time.
12. For each scanned relay position, the system sends an open command.
13. If a locker-open signal is detected, the relay position is marked logically and visually.
14. After scanning, the display updates to show only detected or assigned ports.
15. The installer can use this display as a troubleshooting tool. If a locker fails to open, the installer can see that the number of detected ports is short and can visually identify which door or doors did not open.
16. The installer physically closes each door in the desired locker-numbering sequence.
17. For each closure event, the software assigns the next locker number to the physical door that was closed.
18. The display changes color for the assigned door and provides an audible alert.
19. The installer can visually verify whether all detected locker doors have received locker numbers.
20. During assignment, the software counts the number of commissioned lockers per control board and infers the relative compartment size where supported.
21. The installer clicks a confirm control.
22. The system saves the commissioned topology map, including locker number, branch, relay identifier, derived board/port relationship, size code, and initial status.
23. The commissioning process is finalized with the backend.
24. The commissioning code is no longer valid for security purposes.
25. The kiosk application launches into operational mode and is ready for use.

## 6. Automated Topology Mapping

The core commissioning algorithm creates a physical-to-electrical map without requiring the installer to manually type board/port/locker mappings.

The system models each branch as a set of relay positions. In the current implementation, a branch supports up to 25 boards with 8 ports per board, for 200 relay positions per branch. The system converts a relay identifier to a board/port address using a deterministic mapping:

- board = `((relayId - 1) \ 8) + 1`
- port = `((relayId - 1) Mod 8) + 1`

The installer-facing process is intentionally physical:

- the software opens candidate relay positions;
- responsive doors are detected and marked;
- the installer closes doors in the preferred numbering order;
- each closure event is captured;
- the software binds the next locker number to the branch and relay identifier associated with that closure;
- the relay identifier can then be correlated to board and port.

This decouples physical locker numbering from controller wiring order. The locker numbering sequence does not need to match relay order, port order, board order, manufacturing order, or wiring order.

## 7. Flexible Locker Number Assignment

In many installations, physical locker labels start at the left-most column and move downward from the top door to the bottom door. Numbering then continues at the top of the next column to the right. However, the SmartLockerKiosk commissioning process does not require that convention.

The software assigns locker numbers according to the sequence in which the installer physically closes doors during commissioning. If the customer wants the conventional left-column/top-to-bottom sequence, the installer closes the doors in that sequence. If the customer wants a different sequence, the installer closes the doors in that different sequence. The software records the desired physical numbering sequence while automatically binding each physical door to its branch and relay address.

This makes the process flexible, simple, and customizable while avoiding manual board/port entry.

Potential claim concept:

> Assigning the locker identifier comprises assigning sequential locker numbers according to an order in which an installer physically closes locker doors during commissioning, independent of an electrical wiring order of the locker compartments.

## 8. Visual Troubleshooting During Commissioning

The "Open All" scan serves both discovery and troubleshooting functions.

During the scan, the system sends open commands to relay positions and marks detected locker-open signals. After the scan, the interface displays only detected or assigned ports. This lets the installer compare the expected physical locker count to the displayed detected count. If a door does not open or a port is not detected, the installer can visually identify missing doors before confirming the commissioning map.

During door-closure assignment, each assigned door changes visual state and an audible alert is emitted. This gives the installer immediate feedback that a physical closure was recognized and mapped.

## 9. Size Inference Algorithm

Size inference is valid in installations where each locker column is serviced by a single relay board. The algorithm assumes one relay board per locker column. The locker width and other dimensional information may be predefined in the database locker-size information.

The system determines the number of commissioned doors or active ports associated with a board. The relative compartment size can then be inferred from the count of doors on that board:

- 1 active door on a board corresponds to the largest compartment size.
- 2 active doors correspond to large compartments.
- 3 active doors correspond to medium compartments.
- 4 or 5 active doors correspond to small compartments.
- 6 or 9 active doors correspond to extra-small compartments.

The current implementation maps counts to size codes:

- `1` active assignment -> `E`
- `2` active assignments -> `D`
- `3` active assignments -> `C`
- `4` or `5` active assignments -> `B`
- `6` or `9` active assignments -> `A`

In other embodiments, size may be manually selected, retrieved from predefined configuration, confirmed by an installer, or determined using additional layout metadata.

Potential claim concept:

> Determining the compartment size comprises identifying a relay board associated with a physical locker column, counting a number of commissioned door assignments associated with the relay board, and inferring a compartment size category based on the count.

## 10. Proximity-Aware Locker Selection

The runtime locker assignment algorithm includes an element of physical proximity to the controller or user-access point. Once prerequisites such as size, content type, occupancy state, reservation state, lock state, package/device compatibility, and availability are satisfied, the system chooses an eligible locker based at least partly on physical proximity to the controller.

The commissioned topology map supports this behavior because it records physical locker identifiers and their branch/relay relationships. In some embodiments, physical proximity may be determined or approximated using one or more of:

- branch identifier;
- relay identifier;
- board identifier;
- port identifier;
- locker number;
- column position;
- row position;
- bank position;
- predefined layout metadata;
- distance from the kiosk/controller.

In the current implementation, eligible lockers are filtered by state and ordered by relay identifier. This can represent a proximity preference where relay order corresponds to increasing physical distance from the controller or access point.

Potential claim concept:

> Selecting a locker comprises filtering locker compartments based on compartment size, content type, lock state, occupancy state, and reservation state, and selecting, from the filtered compartments, a locker compartment having a shortest physical proximity to a controller, kiosk, or user-access location.

## 11. Deterministic Physical Governance Platform

The system may be described commercially as a Deterministic Physical Governance Platform. In technical terms, the system governs physical access to locker compartments by tying together:

- commissioned physical topology;
- user or actor identity;
- backend authorization;
- physical locker action;
- local transaction journaling;
- backend acknowledgement;
- audit logging;
- reconciliation of incomplete or failed transactions.

A physical locker action is not treated as a simple local unlock. The kiosk requests or receives authorization, opens a mapped locker compartment, records the local command and result, updates local state, and acknowledges execution to the backend.

Representative authorization request fields include:

- request identifier;
- correlation identifier;
- actor/requested-by identifier;
- site code;
- locker bank identifier;
- locker identifier;
- door identifier;
- action type;
- requested time;
- reason code;
- metadata such as work order identifier.

Representative authorization response fields include:

- transaction identifier;
- command identifier;
- audit event identifier;
- evidence pointer;
- integrity hash;
- server time;
- authorization decision.

Representative acknowledgement fields include:

- transaction identifier;
- command identifier;
- correlation identifier;
- acknowledgement status;
- adapter name;
- hardware event code;
- message;
- affected compartment identifiers.

## 12. Local Transaction Journal and Reconciliation

The kiosk maintains a local transaction journal for physical locker actions. The journal records:

- request ID;
- backend transaction ID;
- backend command ID;
- correlation ID;
- kiosk ID;
- locker ID;
- locker number;
- branch;
- relay ID;
- workflow;
- action type;
- actor ID;
- credential;
- asset tag;
- device type;
- transaction state;
- ACK status;
- retry count;
- timestamps;
- request JSON;
- response JSON;
- last error.

Transaction states include:

- created;
- door open requested;
- door opened;
- local state updated;
- ACK pending;
- ACK failed;
- ACK succeeded;
- completed;
- needs reconciliation;
- resolved;
- cancelled.

This provides deterministic tracking of "who requested what, what physical compartment was affected, what command was issued, whether the backend was acknowledged, and whether manual reconciliation is required."

## 13. Audit Logging

The application includes an audit model for authentication, locker/custody events, administration/configuration, system/security events, monitoring signals, and hardware events.

Audit event fields include:

- timestamp UTC;
- event type;
- actor type;
- actor ID;
- affected component;
- outcome;
- correlation ID;
- reason code.

The current kiosk implementation writes audit events as one JSON object per line to an audit log file under a system-wide application data directory. In broader deployments, corresponding or additional backend audit records may be stored in cloud infrastructure, evidence storage, SIEM, immutable storage, signed logs, hash-chained logs, or other compliance-oriented systems.

Attorney note: backend-side audit immutability, signing, FedRAMP control mapping, and retention mechanisms should be confirmed separately before making strong claims about immutability or FedRAMP compliance.

## 14. Example Independent Claim Themes

These are not final legal claims. They are technical claim themes for counsel.

### A. Automated Commissioning Method

A computer-implemented method for commissioning an electronic locker system, comprising:

- validating a commissioning session using a commissioning code;
- detecting communication ports connected to one or more locker controller branches;
- assigning detected communication ports to physical locker branches;
- scanning relay positions by issuing open commands;
- detecting responsive locker compartments;
- displaying detected locker compartments;
- receiving door-closure events in an installer-selected sequence;
- assigning locker identifiers according to the door-closure sequence;
- associating each locker identifier with a branch and relay identifier;
- deriving board/port data from the relay identifier;
- inferring compartment size based on board-level door count in supported layouts;
- persisting a commissioned topology map; and
- invalidating the commissioning code after successful commissioning.

### B. Closure-Sequence Locker Numbering

A method wherein physical locker numbers are assigned according to an order in which an installer physically closes locker doors, independent of relay wiring order, board order, port order, or physical manufacturing order.

### C. Size Inference From Board Door Count

A method wherein a relay board is associated with a locker column, a count of commissioned door assignments on the relay board is determined, and a compartment size category is inferred based on the count.

### D. Deterministic Physical Governance

A system for governing physical access to commissioned locker compartments, comprising:

- a commissioned topology map correlating locker identifiers to branch and relay addresses;
- a backend authorization service;
- a kiosk application configured to request authorization before physical access;
- a locker controller interface configured to actuate a mapped relay;
- a local transaction journal;
- an acknowledgement mechanism;
- an audit subsystem;
- a reconciliation process for incomplete or failed transactions.

### E. Proximity-Aware Runtime Assignment

A method for assigning a locker during runtime, comprising:

- filtering lockers based on size, content type, occupancy, lock state, reservation state, and availability;
- ranking eligible lockers using physical proximity to a kiosk, controller, or user access point; and
- selecting an eligible locker closest to the controller or access point.

## 15. Potential Dependent Claim Concepts

- The commissioning code is one-time-use and becomes invalid after finalization.
- The kiosk downloads location, branding, workflow, kiosk ID, runtime settings, and commissioning policy from the backend.
- A detected communication port is assigned to a left branch, right branch, Branch A, Branch B, or another physical branch identifier.
- Each branch includes multiple daisy-chained relay boards.
- The scan count can be reduced by the installer to reduce commissioning time.
- The interface displays detected relay positions and hides undetected relay positions after scanning.
- The interface provides visual troubleshooting by showing fewer detected ports than expected.
- An audible alert confirms each successful door-closure assignment.
- The software prevents duplicate branch/relay assignments.
- The software prevents duplicate locker-number assignments.
- The commissioned topology map is stored locally and used for runtime physical access.
- The topology map is registered or finalized with a backend service.
- Locker open commands are journaled before or during physical actuation.
- Failed locker actions are marked as requiring reconciliation.
- Backend ACK failure does not cause the kiosk to reopen the locker.
- The authorization response includes transaction ID, command ID, audit event ID, evidence pointer, and integrity hash.
- Audit records include actor, affected component, outcome, correlation ID, and reason code.
- Locker assignment uses proximity after satisfying size and content prerequisites.

## 16. Advantages Over Known Processes

The disclosed system may provide the following advantages:

- reduces or eliminates manual board/port/locker mapping;
- reduces need for simultaneous on-site and backend technician coordination;
- allows an installer to configure numbering by physically closing doors in sequence;
- supports customized numbering schemes without rewiring;
- provides immediate visual troubleshooting during commissioning;
- can infer size automatically in supported physical layouts;
- turns commissioning into a backend-controlled, one-time-code process;
- creates a trusted topology map used later for authorization, audit, and locker assignment;
- supports proximity-aware runtime locker selection;
- improves traceability of physical access events.

## 17. Information Counsel Should Confirm

The following items should be confirmed before filing:

- Dates of conception, first prototype, first working commissioning flow, first customer demo, first sale, and any public disclosure.
- Names and contributions of all inventors.
- Whether any backend-side systems implement signed, hash-chained, immutable, or FedRAMP-controlled audit storage.
- Whether `integrityHashSha256` and `evidencePointer` are generated by production backend services and how they are stored.
- Whether the commissioning code is currently invalidated server-side after finalization.
- Whether proximity is currently based on relay order alone or richer layout metadata.
- Whether size inference has been tested across all intended locker-bank layouts.
- Whether any third-party controller hardware manuals or SDKs impose limitations on claim scope.
- Whether competitor systems use any similar door-closure assignment method.

## 18. Recommended Drawings

Recommended patent drawings include:

1. System architecture: kiosk, backend, database, controller, branches, relay boards, locker compartments.
2. Commissioning flowchart from power-on through code invalidation.
3. Branch and daisy-chained relay-board topology.
4. Open-scan detection screen showing detected vs. missing ports.
5. Door-closure sequence assignment flow.
6. Relay ID to board/port mapping diagram.
7. Size inference diagram showing one board per column and different door counts.
8. Runtime authorization and ACK sequence diagram.
9. Transaction journal state machine.
10. Proximity-aware locker selection flow.

## 19. Draft Abstract

An electronic locker system includes a guided commissioning workflow for automatically creating a physical topology map of locker compartments. A kiosk validates a commissioning code, obtains backend configuration, detects controller communication ports, associates the ports with physical locker branches, scans relay positions by issuing open commands, detects responsive locker compartments, and receives door-closure events in an installer-selected sequence. The system assigns locker identifiers according to the closure sequence, associates each locker identifier with a branch and relay identifier, derives board and port information, and infers compartment size in supported layouts based on board-level door count. The resulting topology map is persisted and used for deterministic physical access governance, including backend authorization, physical locker actuation, transaction journaling, acknowledgement, audit logging, reconciliation, and proximity-aware locker assignment.

