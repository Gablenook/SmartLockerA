Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Reflection
Imports System.Security.Authentication
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Namespace SmartLockerKiosk
    Partial Public Class CommissioningAccessWindow
        Inherits Window

        Public Property ActorId As String
        Public Property KioskId As String
        Private _lockerController As LockerControllerService
        Private _networkOk As Boolean = False
        Private _idValidated As Boolean = False
        Private _commissioningToken As String = Nothing  ' optional: backend can return short-lived token
        Private Const BackendBaseUrl As String = "https://smartlockerapp.azurewebsites.net"
        Private Const HealthPath As String = "/api/health"  ' adjust to your real endpoint
        Private Const NetworkTimeoutMs As Integer = 3000
        Public Property TenantId As String
        Public Property OrgNodeId As String
        Public Property CommissioningSessionId As String
        Public Property KioskName As String
        Public Property OrganizationName As String
        Public Property LogoUri As String
        Public Property SecondaryLogoUri As String
        Public Property AccentColor As String

        Private ReadOnly _commissioningService As ICommissioningService

        Private Const LocalBypassCommissioningCode As String = "12345"
        Private Const EnableBackendCommissioning As Boolean = False

        ' Network gating for Validate
        Private _networkChecked As Boolean = False
        Private _internetOk As Boolean = False

        ' If you want to allow offline commissioning, flip this to True (or pull from AppSettings)
        Private Const AllowOfflineCommissioning As Boolean = False

        ' Optional: store for UI display or audit
        Private _networkDiag As String = ""

        Private _restoreTopmostAfterSettings As Boolean = False
        Private _openingWifiSettings As Boolean = False
        Private _restoreTimer As DispatcherTimer = Nothing
        Private _settingsProcess As Process = Nothing

        Public Sub New()
            InitializeComponent()

            If EnableBackendCommissioning Then
                _commissioningService = New BackendCommissioningService()
            Else
                _commissioningService = New BypassCommissioningService(LocalBypassCommissioningCode)
            End If
        End Sub
        Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            ' Clamp to the current screen so debug runs don't look weird
            Dim maxW = SystemParameters.WorkArea.Width
            Dim maxH = SystemParameters.WorkArea.Height
            If Me.Width > maxW Then Me.Width = maxW
            If Me.Height > maxH Then Me.Height = maxH

            ' Safety: commissioning should only be reachable pre-commissioning
            Using db = DatabaseBootstrapper.BuildDbContext()
                Dim row = db.KioskState.AsNoTracking().SingleOrDefault()
                If row IsNot Nothing AndAlso row.IsCommissioned Then
                    MessageBox.Show("This kiosk is already commissioned.")
                    Application.Current.Shutdown()
                    Return
                End If
            End Using

            ' Initial UI state for the new workflow
            _networkChecked = False
            _internetOk = False
            _networkOk = False
            _idValidated = False

            NetworkSummaryText.Text = "Network status: not checked."
            NetworksListBox.ItemsSource = Nothing
            ValidateButton.IsEnabled = False

            StatusText.Text = "Step 1: Refresh Networks. Step 2: Enter Commissioning ID and Validate."
        End Sub
        Private Async Sub CheckNetwork_Click(sender As Object, e As RoutedEventArgs)
            CheckNetworkButton.IsEnabled = False
            ValidateButton.IsEnabled = False

            _networkChecked = True
            _internetOk = False
            _networkOk = False
            _networkDiag = ""

            Try
                StatusText.Text = "Checking network…"

                Dim ethernetUp = IsEthernetUp()
                Dim wifiPresent = IsWifiAdapterPresent()

                ' Internet probe (fast)
                _internetOk = Await ProbeInternetAsync()

                ' Populate Wi-Fi list
                Dim nets As List(Of WifiNetworkItem) = New List(Of WifiNetworkItem)()
                If wifiPresent Then
                    nets = GetWifiNetworksFromNetsh()
                End If

                NetworksListBox.ItemsSource = nets
                If nets.Count > 0 Then NetworksListBox.SelectedIndex = 0

                Dim ethText = If(ethernetUp, "Ethernet: Connected", "Ethernet: Not connected")
                Dim wifiText = If(wifiPresent, $"Wi-Fi: Available ({nets.Count} found)", "Wi-Fi: Not present")
                Dim netText = If(_internetOk, "Internet: OK", "Internet: Not reachable")

                NetworkSummaryText.Text = $"{ethText}  |  {wifiText}  |  {netText}"

                ' Decide what "network ok" means for commissioning
                _networkOk = _internetOk OrElse AllowOfflineCommissioning

                ValidateButton.IsEnabled = _networkOk

                If _networkOk Then
                    StatusText.Text = "Network check complete. Enter Commissioning ID, then Validate."
                Else
                    StatusText.Text = "Internet not reachable. Connect to Wi-Fi/Ethernet (Open Wi-Fi Settings), then Refresh Networks."
                End If

            Catch ex As Exception
                NetworkSummaryText.Text = "Network status: error."
                NetworksListBox.ItemsSource = Nothing
                _networkOk = AllowOfflineCommissioning
                ValidateButton.IsEnabled = _networkOk
                StatusText.Text = "Network check failed: " & ex.Message

            Finally
                CheckNetworkButton.IsEnabled = True
            End Try
        End Sub
        Private Async Sub OpenWifiSettings_Click(sender As Object, e As RoutedEventArgs)
            If _openingWifiSettings Then Return
            _openingWifiSettings = True

            Try
                _restoreTopmostAfterSettings = Me.Topmost

                ' Drop Topmost and minimize so Settings can be seen
                Me.Topmost = False
                Me.WindowState = WindowState.Normal       ' ensure visible

                ' Launch Wi-Fi settings (do NOT rely on Process object for ms-settings URIs)
                Dim psi As New ProcessStartInfo("ms-settings:network-wifi") With {.UseShellExecute = True}
                Process.Start(psi)

                ' Failsafe restore monitor (restores after N seconds even if Settings can't be tracked)
                StartRestoreMonitor()

                Await Task.Delay(350)

            Catch ex As Exception
                StatusText.Text = "Unable to open Wi-Fi settings: " & ex.Message
                Try
                    Me.WindowState = WindowState.Normal
                    Me.Topmost = True
                Catch
                End Try
            Finally
                _openingWifiSettings = False
            End Try
        End Sub
        Private Sub NetworksListBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Dim sel = TryCast(NetworksListBox.SelectedItem, WifiNetworkItem)
            If sel Is Nothing Then Return

            StatusText.Text = $"Selected Wi-Fi: {sel.Ssid}. Use Open Wi-Fi Settings to connect if needed."
        End Sub
        Private Function IsEthernetUp() As Boolean
            Try
                For Each ni In System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    If ni.OperationalStatus <> System.Net.NetworkInformation.OperationalStatus.Up Then Continue For
                    If ni.NetworkInterfaceType = System.Net.NetworkInformation.NetworkInterfaceType.Ethernet Then
                        Return True
                    End If
                Next
            Catch
            End Try
            Return False
        End Function
        Private Function IsWifiAdapterPresent() As Boolean
            Try
                For Each ni In System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    If ni.NetworkInterfaceType = System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 Then
                        Return True
                    End If
                Next
            Catch
            End Try
            Return False
        End Function
        Private Async Function ProbeInternetAsync() As Task(Of Boolean)
            Try
                Using client As New Net.Http.HttpClient()
                    client.Timeout = TimeSpan.FromSeconds(2.5)
                    ' Lightweight connectivity check (no big download)
                    Dim resp = Await client.GetAsync("http://www.msftconnecttest.com/connecttest.txt")
                    Return resp.IsSuccessStatusCode
                End Using
            Catch
                Return False
            End Try
        End Function
        Private Function GetWifiNetworksFromNetsh() As List(Of WifiNetworkItem)
            Dim results As New List(Of WifiNetworkItem)()

            Dim psi As New ProcessStartInfo("netsh", "wlan show networks mode=bssid") With {
        .RedirectStandardOutput = True,
        .RedirectStandardError = True,
        .UseShellExecute = False,
        .CreateNoWindow = True
    }

            Dim output As String = ""
            Using p As Process = Process.Start(psi)
                output = p.StandardOutput.ReadToEnd()
                p.WaitForExit(3000)
            End Using

            ' Parse blocks: "SSID n : <name>", then "Signal : xx%", "Authentication : <type>"
            Dim lines = output.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)

            Dim currentSsid As String = Nothing
            Dim currentSignal As String = Nothing
            Dim currentAuth As String = Nothing

            For Each raw In lines
                Dim line = raw.Trim()

                If line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase) AndAlso line.Contains(":") Then
                    ' Commit previous
                    If Not String.IsNullOrWhiteSpace(currentSsid) Then
                        results.Add(New WifiNetworkItem With {
                    .Ssid = currentSsid,
                    .SignalText = If(currentSignal, ""),
                    .SecurityText = If(currentAuth, "")
                })
                    End If

                    currentSsid = line.Substring(line.IndexOf(":"c) + 1).Trim()
                    currentSignal = Nothing
                    currentAuth = Nothing
                    Continue For
                End If

                If line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) AndAlso line.Contains(":") AndAlso currentSignal Is Nothing Then
                    currentSignal = line.Substring(line.IndexOf(":"c) + 1).Trim()
                    Continue For
                End If

                If line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) AndAlso line.Contains(":") AndAlso currentAuth Is Nothing Then
                    currentAuth = line.Substring(line.IndexOf(":"c) + 1).Trim()
                    Continue For
                End If
            Next

            ' Commit last
            If Not String.IsNullOrWhiteSpace(currentSsid) Then
                results.Add(New WifiNetworkItem With {
            .Ssid = currentSsid,
            .SignalText = If(currentSignal, ""),
            .SecurityText = If(currentAuth, "")
        })
            End If

            ' Clean up: remove blank SSIDs, de-dupe by name, sort
            results = results.Where(Function(x) Not String.IsNullOrWhiteSpace(x.Ssid)).
                      GroupBy(Function(x) x.Ssid, StringComparer.OrdinalIgnoreCase).
                      Select(Function(g) g.First()).
                      OrderByDescending(Function(x) ParseSignalPercent(x.SignalText)).
                      ThenBy(Function(x) x.Ssid, StringComparer.OrdinalIgnoreCase).
                      ToList()

            Return results
        End Function
        Private Function ParseSignalPercent(signalText As String) As Integer
            If String.IsNullOrWhiteSpace(signalText) Then Return 0
            Dim s = signalText.Replace("%", "").Trim()
            Dim n As Integer
            If Integer.TryParse(s, n) Then Return n
            Return 0
        End Function
        Private Sub Window_Activated(sender As Object, e As EventArgs) Handles Me.Activated
            ' If we minimized for Settings, bring back to normal
            If Me.WindowState = WindowState.Minimized Then
                Me.WindowState = WindowState.Normal
            End If

            ' Restore Topmost once, when returning from Settings
            If _restoreTopmostAfterSettings Then
                Me.Topmost = True
                _restoreTopmostAfterSettings = False
            End If

            ' Ensure we actually take focus again
            Try
                Me.Activate()
                Me.Focus()
            Catch
            End Try
        End Sub
        Private Sub StartRestoreMonitor()
            If _restoreTimer IsNot Nothing Then
                _restoreTimer.Stop()
                _restoreTimer = Nothing
            End If

            Dim startedUtc = DateTime.UtcNow

            _restoreTimer = New DispatcherTimer()
            _restoreTimer.Interval = TimeSpan.FromMilliseconds(500)

            AddHandler _restoreTimer.Tick,
        Sub()
            ' If we are already back, stop monitoring
            If Me.WindowState <> WindowState.Minimized Then
                _restoreTimer.Stop()
                _restoreTimer = Nothing
                Return
            End If

            ' Hard failsafe: restore after 15 seconds no matter what
            If (DateTime.UtcNow - startedUtc) > TimeSpan.FromSeconds(15) Then
                _restoreTimer.Stop()
                _restoreTimer = Nothing
                RestoreFromSettings()
            End If
        End Sub

            _restoreTimer.Start()
        End Sub
        Private Sub RestoreFromSettings()
            Try
                If Me.WindowState = WindowState.Minimized Then
                    Me.WindowState = WindowState.Normal
                End If

                Me.Topmost = True
                _restoreTopmostAfterSettings = False

                Me.Activate()
                Me.Focus()

                StatusText.Text = "Back from Wi-Fi Settings. Press Refresh Networks."
            Catch
            End Try
        End Sub
        Private Sub Window_Deactivated(sender As Object, e As EventArgs) Handles Me.Deactivated
            ' Soft-nudge only; don't fight too hard or you'll annoy installers.
            StatusText.Text = "Finish Wi-Fi setup, then return here and press Refresh Networks."
        End Sub
        Private Async Sub Validate_Click(sender As Object, e As RoutedEventArgs)
            If Not _networkChecked AndAlso Not AllowOfflineCommissioning Then
                StatusText.Text = "Please run Refresh Networks first."
                Return
            End If

            If (Not _networkOk) AndAlso Not AllowOfflineCommissioning Then
                StatusText.Text = "Network not ready. Connect to Wi-Fi/Ethernet, then Refresh Networks."
                Return
            End If

            Dim id = (If(CommissioningIdTextBox.Text, "")).Trim()
            If id.Length = 0 Then
                StatusText.Text = "Enter Commissioning ID."
                Return
            End If

            If Not id.All(Function(ch) Char.IsDigit(ch)) Then
                StatusText.Text = "Commissioning ID must be numeric."
                _idValidated = False
                Return
            End If

            ValidateButton.IsEnabled = False
            CheckNetworkButton.IsEnabled = False

            Try
                StatusText.Text = "Validating Commissioning ID..."

                Dim result = Await _commissioningService.BeginCommissioningAsync(id, CancellationToken.None)

                If result Is Nothing Then
                    _idValidated = False
                    StatusText.Text = "No response from commissioning service."
                    Return
                End If

                If Not result.Ok Then
                    _idValidated = False

                    If result.Error IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(result.Error.message) Then
                        StatusText.Text = result.Error.message
                    Else
                        StatusText.Text = "Commissioning validation failed."
                    End If

                    Return
                End If

                If result.Data Is Nothing Then
                    _idValidated = False
                    StatusText.Text = "Commissioning validation returned no data."
                    Return
                End If

                If Not result.Data.validated Then
                    _idValidated = False
                    StatusText.Text = "Invalid Commissioning ID."
                    Return
                End If

                ' Capture returned context for later commissioning steps
                _idValidated = True
                _commissioningToken = result.Data.commissioningToken

                ActorId = result.Data.actorId
                KioskId = result.Data.kioskId
                TenantId = result.Data.tenantId
                OrgNodeId = result.Data.orgNodeId
                CommissioningSessionId = result.Data.commissioningSessionId

                KioskName = result.Data.kioskName
                If result.Data.branding IsNot Nothing Then
                    OrganizationName = result.Data.branding.organizationName
                    LogoUri = result.Data.branding.logoUri
                    SecondaryLogoUri = result.Data.branding.secondaryLogoUri
                    AccentColor = result.Data.branding.accentColor
                End If

                StatusText.Text = "Commissioning ID validated. Proceeding..."
                GoToControllerSetup()

            Catch ex As Exception
                _idValidated = False
                StatusText.Text = "Validation failed: " & ex.Message

            Finally
                ValidateButton.IsEnabled = _networkOk
                CheckNetworkButton.IsEnabled = True
            End Try
        End Sub
        Private Async Sub GoToControllerSetup()
            If Not _networkOk AndAlso Not AllowOfflineCommissioning Then
                StatusText.Text = "Network not ready. Complete Step 1 first."
                Return
            End If

            If Not _idValidated Then
                StatusText.Text = "Commissioning ID not validated. Complete Step 2."
                Return
            End If

            Dim effectiveActorId = If(String.IsNullOrWhiteSpace(ActorId), "System:Commissioning", ActorId)

            ' 1) Ports setup
            Try
                StatusText.Text = "Opening Controller Setup..."

                Dim portsWin As New ControllerSetupWindow() With {
            .ActorId = effectiveActorId
        }
                portsWin.Owner = Me
                portsWin.WindowStartupLocation = WindowStartupLocation.CenterOwner
                portsWin.Topmost = True

                Dim portsOk = portsWin.ShowDialog()
                If portsOk <> True Then
                    StatusText.Text = "Controller setup was not completed. Please save Branch ports to continue."
                    Return
                End If

                StatusText.Text = "Controller setup complete."

            Catch ex As Exception
                StatusText.Text = "ControllerSetupWindow failed: " & ex.GetType().Name & ": " & ex.Message
                MessageBox.Show(ex.ToString(), "ControllerSetupWindow exception")
                Return
            End Try

            ' 2) Ensure Branch A is configured
            Dim aPort As String = Nothing
            Using db = DatabaseBootstrapper.BuildDbContext()
                aPort = db.ControllerPorts.AsNoTracking().
            SingleOrDefault(Function(r) r.BranchName = "A")?.PortName
            End Using

            If String.IsNullOrWhiteSpace(aPort) Then
                StatusText.Text = "Branch A port is required before locker commissioning."
                Return
            End If

            If _lockerController Is Nothing Then
                _lockerController = New LockerControllerService()
            End If

            ' 3) Locker commissioning
            Dim lockerWin As New LockerCommissioningWindow(_lockerController) With {
        .ActorId = effectiveActorId,
        .KioskId = KioskId
    }
            lockerWin.Owner = Me
            lockerWin.WindowStartupLocation = WindowStartupLocation.CenterOwner

            Dim lockerOk = lockerWin.ShowDialog()
            If lockerOk <> True Then
                StatusText.Text = "Locker commissioning was not completed."
                Return
            End If

            StatusText.Text = "Local commissioning complete. Registering kiosk health..."

            ' 4) Register commissioning health
            Dim appVersion As String = ""
            Try
                appVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString()
            Catch
                appVersion = ""
            End Try

            Dim healthRequest As New RegisterCommissioningHealthRequest With {
        .commissioningSessionId = CommissioningSessionId,
        .kioskId = KioskId,
        .tenantId = TenantId,
        .orgNodeId = OrgNodeId,
        .deviceInfo = New CommissioningDeviceInfo With {
            .machineName = Environment.MachineName,
            .deviceName = If(KioskName, ""),
            .osVersion = Environment.OSVersion.ToString(),
            .appVersion = appVersion,
            .hardwareFingerprint = Nothing
        },
        .networkStatus = New CommissioningNetworkStatusDTO With {
            .internetReachable = _internetOk,
            .backendReachable = _networkOk
        },
        .healthStatus = New CommissioningHealthStatusDTO With {
            .applicationStarted = True,
            .localDatabaseInitialized = True,
            .keypadAvailable = True
        },
        .reportedAtUtc = DateTime.UtcNow
    }

            Dim healthResult As ApiResult(Of RegisterCommissioningHealthResponse) = Nothing

            Try
                healthResult = Await _commissioningService.RegisterHealthAsync(healthRequest, CancellationToken.None)
            Catch ex As Exception
                StatusText.Text = "Health registration failed: " & ex.Message
                Return
            End Try

            If healthResult Is Nothing Then
                StatusText.Text = "Health registration returned no response."
                Return
            End If

            If Not healthResult.Ok OrElse healthResult.Data Is Nothing OrElse Not healthResult.Data.registered Then
                If healthResult.Error IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(healthResult.Error.message) Then
                    StatusText.Text = healthResult.Error.message
                Else
                    StatusText.Text = "Health registration failed."
                End If
                Return
            End If

            StatusText.Text = "Health registered. Finalizing commissioning..."

            ' 5) Finalize commissioning
            Dim finalizeRequest As New FinalizeCommissioningRequest With {
        .commissioningSessionId = CommissioningSessionId,
        .kioskId = KioskId,
        .tenantId = TenantId,
        .orgNodeId = OrgNodeId,
        .actorId = ActorId,
        .deviceInfo = New CommissioningDeviceInfo With {
            .machineName = Environment.MachineName,
            .deviceName = If(KioskName, ""),
            .osVersion = Environment.OSVersion.ToString(),
            .appVersion = appVersion,
            .hardwareFingerprint = Nothing
        },
        .commissioningResult = New FinalizeCommissioningResultDTO With {
            .controllerSetupCompleted = True,
            .lockerMappingCompleted = True,
            .healthRegistrationCompleted = True,
            .localDatabaseInitialized = True
        }
    }

            Dim finalizeResult As ApiResult(Of FinalizeCommissioningResponse) = Nothing

            Try
                finalizeResult = Await _commissioningService.FinalizeCommissioningAsync(finalizeRequest, CancellationToken.None)
            Catch ex As Exception
                StatusText.Text = "Commissioning finalization failed: " & ex.Message
                Return
            End Try

            If finalizeResult Is Nothing Then
                StatusText.Text = "Finalization returned no response."
                Return
            End If

            If Not finalizeResult.Ok OrElse finalizeResult.Data Is Nothing OrElse Not finalizeResult.Data.commissioned Then
                If finalizeResult.Error IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(finalizeResult.Error.message) Then
                    StatusText.Text = finalizeResult.Error.message
                Else
                    StatusText.Text = "Commissioning finalization failed."
                End If
                Return
            End If

            ' 6) Persist final commissioned state locally
            Try
                Using db = DatabaseBootstrapper.BuildDbContext()
                    Dim ks = db.KioskState.SingleOrDefault(Function(x) x.KioskId = KioskId)

                    If ks Is Nothing Then
                        ks = New KioskState With {
                    .KioskId = KioskId
                }
                        db.KioskState.Add(ks)
                    End If

                    ks.LocationId = AppSettings.LocationId
                    ks.TenantId = TenantId
                    ks.OrgNodeId = OrgNodeId
                    ks.CommissioningSessionId = CommissioningSessionId
                    ks.KioskName = KioskName
                    ks.OrganizationName = OrganizationName
                    ks.LogoUri = LogoUri
                    ks.SecondaryLogoUri = SecondaryLogoUri
                    ks.AccentColor = AccentColor
                    ks.KioskToken = finalizeResult.Data.kioskToken
                    ks.RefreshToken = finalizeResult.Data.refreshToken
                    ks.IsCommissioned = True
                    ks.CommissionedUtc = finalizeResult.Data.commissionedAtUtc
                    ks.CommissionedBy = ActorId
                    ks.LastHealthRegistrationUtc = healthResult.Data.registeredAtUtc
                    ks.LastUpdatedUtc = DateTime.UtcNow

                    db.SaveChanges()
                End Using

            Catch ex As Exception
                StatusText.Text = "Commissioning finalized, but local kiosk state save failed: " & ex.Message
                Return
            End Try

            StatusText.Text = "Commissioning finalized successfully."
            Me.DialogResult = True
            Me.Close()
        End Sub
        Private Sub Cancel_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub

    End Class
End Namespace
