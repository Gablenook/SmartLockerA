# SmartLockerKiosk Patent Drawing Set

Prepared as editable conceptual drawings for patent counsel. These are not formal USPTO line drawings, but they are structured so a patent illustrator can convert them into formal figures.

## Figure 1 - System Architecture

```mermaid
flowchart LR
    Installer["Installer / Technician"]
    Kiosk["SmartLockerKiosk<br/>Kiosk Application"]
    LocalDb["Local Kiosk Database<br/>Topology, Status, Journal"]
    Backend["Backend Services<br/>Commissioning, Auth, Audit"]
    AuditStore["Audit / Evidence Store"]
    BranchA["Branch A<br/>Left Locker Branch"]
    BranchB["Branch B<br/>Right Locker Branch"]
    BoardA1["Relay Board A1"]
    BoardA2["Relay Board A2"]
    BoardB1["Relay Board B1"]
    DoorsA["Locker Compartments<br/>A Branch"]
    DoorsB["Locker Compartments<br/>B Branch"]

    Installer --> Kiosk
    Kiosk <--> Backend
    Backend --> AuditStore
    Kiosk <--> LocalDb
    Kiosk --> BranchA
    Kiosk --> BranchB
    BranchA --> BoardA1 --> BoardA2 --> DoorsA
    BranchB --> BoardB1 --> DoorsB
```

## Figure 2 - Commissioning Flow

```mermaid
flowchart TD
    Start["Power locker system"]
    Boot["Kiosk boots to commissioning screen"]
    Network["Validate network connection"]
    Code["Enter commissioning code"]
    Validate["Backend validates commissioning session"]
    Download["Download branding, workflow,<br/>location, kiosk ID, policy"]
    DetectPorts["Detect USB / COM controller ports"]
    AssignBranches["Assign detected ports to Branch A / Branch B"]
    ConnectBoards["Connect to relay boards"]
    OpenScan["Run Open All scan"]
    DetectDoors["Detect responsive locker doors"]
    Troubleshoot["Display detected doors<br/>for troubleshooting"]
    CloseSeq["Close doors in desired numbering sequence"]
    InferSize["Infer size where supported"]
    Confirm["Confirm commissioning map"]
    Persist["Persist topology and initial status"]
    Finalize["Finalize with backend"]
    Invalidate["Invalidate commissioning code"]
    Launch["Launch operational kiosk mode"]

    Start --> Boot --> Network --> Code --> Validate --> Download
    Download --> DetectPorts --> AssignBranches --> ConnectBoards
    ConnectBoards --> OpenScan --> DetectDoors --> Troubleshoot
    Troubleshoot --> CloseSeq --> InferSize --> Confirm --> Persist
    Persist --> Finalize --> Invalidate --> Launch
```

## Figure 3 - Branch and Daisy-Chained Relay Board Topology

```mermaid
flowchart TB
    Kiosk["Kiosk / Controller"]
    LeftPort["COM Port assigned to Branch A<br/>(Left Branch)"]
    RightPort["COM Port assigned to Branch B<br/>(Right Branch)"]

    A1["Relay Board A1<br/>Ports 1-8"]
    A2["Relay Board A2<br/>Ports 9-16"]
    A3["Relay Board A3<br/>Ports 17-24"]
    B1["Relay Board B1<br/>Ports 1-8"]
    B2["Relay Board B2<br/>Ports 9-16"]

    ADoors["Left branch locker doors"]
    BDoors["Right branch locker doors"]

    Kiosk --> LeftPort --> A1 --> A2 --> A3 --> ADoors
    Kiosk --> RightPort --> B1 --> B2 --> BDoors
```

## Figure 4 - Open-Scan Detection and Troubleshooting

```mermaid
flowchart TD
    Begin["Start Open All scan"]
    SetRange["Set relay scan range<br/>default or reduced"]
    Relay["Select next branch / relay position"]
    Command["Send open command"]
    Signal{"Open signal detected?"}
    MarkDetected["Mark relay position as detected<br/>logically and visually"]
    MarkMissing["Leave relay position undetected"]
    More{"More relay positions?"}
    Filter["Display detected positions only"]
    Compare["Installer compares detected count<br/>to expected physical doors"]
    Missing{"Any missing doors?"}
    Troubleshoot["Troubleshoot wiring, power,<br/>lock, sensor, or controller"]
    Continue["Continue to door-closure assignment"]

    Begin --> SetRange --> Relay --> Command --> Signal
    Signal -->|Yes| MarkDetected --> More
    Signal -->|No| MarkMissing --> More
    More -->|Yes| Relay
    More -->|No| Filter --> Compare --> Missing
    Missing -->|Yes| Troubleshoot --> Begin
    Missing -->|No| Continue
```

## Figure 5 - Door-Closure Sequence Assignment

```mermaid
flowchart TD
    Ready["Detected doors displayed"]
    Assign["Enter Assign Numbers mode"]
    Prompt["Prompt installer to close locker #N"]
    Close["Installer closes physical door"]
    Detect["System detects transition<br/>open to closed"]
    Validate{"Door detected and unassigned?"}
    Map["Assign current locker number N<br/>to branch + relay ID"]
    Feedback["Change visual color<br/>and play audible alert"]
    Increment["Increment N"]
    Complete{"All detected doors assigned?"}
    Confirm["Enable Confirm button"]
    Ignore["Ignore duplicate or invalid closure"]

    Ready --> Assign --> Prompt --> Close --> Detect --> Validate
    Validate -->|Yes| Map --> Feedback --> Increment --> Complete
    Validate -->|No| Ignore --> Prompt
    Complete -->|No| Prompt
    Complete -->|Yes| Confirm
```

## Figure 6 - Relay ID to Board and Port Mapping

```mermaid
flowchart LR
    Relay["Relay ID<br/>1..200"]
    Formula1["Board = ((RelayID - 1) div 8) + 1"]
    Formula2["Port = ((RelayID - 1) mod 8) + 1"]
    BoardPort["Derived Board / Port"]
    Topology["Commissioned Topology Record"]

    Relay --> Formula1 --> BoardPort
    Relay --> Formula2 --> BoardPort
    BoardPort --> Topology

    Topology --> Fields["LockerNumber<br/>Branch<br/>RelayID<br/>Board<br/>Port<br/>SizeCode"]
```

## Figure 7 - Size Inference From One Board Per Column

```mermaid
flowchart TD
    Board["Relay board associated<br/>with one locker column"]
    Count["Count active commissioned doors<br/>on the board"]
    Decision{"Active door count"}
    One["1 door<br/>largest compartment"]
    Two["2 doors<br/>large compartments"]
    Three["3 doors<br/>medium compartments"]
    FourFive["4 or 5 doors<br/>small compartments"]
    SixNine["6 or 9 doors<br/>extra-small compartments"]
    Store["Store inferred SizeCode<br/>with locker records"]

    Board --> Count --> Decision
    Decision -->|1| One --> Store
    Decision -->|2| Two --> Store
    Decision -->|3| Three --> Store
    Decision -->|4 or 5| FourFive --> Store
    Decision -->|6 or 9| SixNine --> Store
```

## Figure 8 - Runtime Authorization and Acknowledgement

```mermaid
sequenceDiagram
    participant User as User / Actor
    participant Kiosk as Kiosk App
    participant Backend as Backend Authorization
    participant Journal as Local Transaction Journal
    participant Controller as Locker Controller
    participant Audit as Audit / Evidence

    User->>Kiosk: Present credential / request access
    Kiosk->>Backend: Authorize locker action
    Backend->>Audit: Record authorization decision
    Backend-->>Kiosk: transactionId, commandId, decision
    Kiosk->>Journal: Create transaction record
    Kiosk->>Controller: Open mapped branch / relay
    Controller-->>Kiosk: Command result
    Kiosk->>Journal: Update door-open / local state
    Kiosk->>Backend: ACK physical execution
    Backend->>Audit: Record ACK / evidence
    Backend-->>Kiosk: ACK result
    Kiosk->>Journal: Mark ACK succeeded or failed
```

## Figure 9 - Transaction Journal State Machine

```mermaid
stateDiagram-v2
    [*] --> Created
    Created --> DoorOpenRequested
    DoorOpenRequested --> DoorOpened
    DoorOpenRequested --> NeedsReconciliation: open failed
    DoorOpened --> LocalStateUpdated
    LocalStateUpdated --> AckPending
    AckPending --> AckSucceeded: backend ACK accepted
    AckPending --> AckFailed: backend ACK failed
    AckFailed --> AckPending: retry
    AckFailed --> NeedsReconciliation: retry limit / error
    AckSucceeded --> Completed
    NeedsReconciliation --> Resolved: admin review
    NeedsReconciliation --> Cancelled: cancelled
    Resolved --> [*]
    Completed --> [*]
    Cancelled --> [*]
```

## Figure 10 - Proximity-Aware Locker Selection

```mermaid
flowchart TD
    Request["Locker request<br/>size, content type, workflow"]
    Load["Load commissioned locker topology<br/>and current locker status"]
    FilterSize["Filter by required size"]
    FilterContent["Filter by content / device type"]
    FilterState["Filter by lock state, occupancy,<br/>reservation, package presence"]
    Candidates{"Eligible lockers remain?"}
    Rank["Rank eligible lockers by physical proximity<br/>to kiosk / controller / access point"]
    Select["Select closest eligible locker"]
    Reserve["Reserve or assign selected locker"]
    Deny["Return no available locker"]

    Request --> Load --> FilterSize --> FilterContent --> FilterState --> Candidates
    Candidates -->|Yes| Rank --> Select --> Reserve
    Candidates -->|No| Deny
```

## Figure 11 - Integrated Commissioned Governance Loop

```mermaid
flowchart TD
    Commission["Commissioning creates trusted topology map"]
    Topology["Topology map<br/>LockerNumber + Branch + Relay + Board/Port + Size"]
    Assignment["Runtime assignment<br/>availability + size + proximity"]
    Authorization["Deterministic authorization<br/>identity + policy + locker"]
    Actuation["Physical actuation<br/>mapped branch / relay"]
    Journal["Local transaction journal"]
    Ack["Backend acknowledgement"]
    Audit["Audit and evidence records"]
    Reconcile["Reconciliation for failures<br/>or incomplete transactions"]

    Commission --> Topology
    Topology --> Assignment
    Assignment --> Authorization
    Authorization --> Actuation
    Actuation --> Journal
    Journal --> Ack
    Ack --> Audit
    Journal --> Reconcile
    Reconcile --> Audit
```

## Figure 12 - Example Physical Numbering Sequence

```mermaid
flowchart TB
    subgraph Column1["Column 1"]
        L1["Locker 1<br/>top"]
        L2["Locker 2"]
        L3["Locker 3<br/>bottom"]
    end

    subgraph Column2["Column 2"]
        L4["Locker 4<br/>top"]
        L5["Locker 5"]
        L6["Locker 6<br/>bottom"]
    end

    subgraph Column3["Column 3"]
        L7["Locker 7<br/>top"]
        L8["Locker 8"]
        L9["Locker 9<br/>bottom"]
    end

    L1 --> L2 --> L3 --> L4 --> L5 --> L6 --> L7 --> L8 --> L9
```

Note: Figure 12 shows a conventional numbering sequence. The commissioning system may assign any desired numbering order because the number is assigned according to the sequence in which the installer closes physical doors.

