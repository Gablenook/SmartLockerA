Imports System.Diagnostics
Imports System.Reflection
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports System.Windows.Threading
Imports Microsoft.EntityFrameworkCore

Namespace SmartLockerKiosk

    Partial Public Class CommissioningAccessWindow
        Inherits Window

        Public Property ActorId As String
        Public Property KioskId As String

        Public Property TenantId As String
        Public Property OrgNodeId As String
        Public Property CommissioningSessionId As String
        Public Property KioskName As String
        Public Property OrganizationName As String
        Public Property LogoUri As String
        Public Property SecondaryLogoUri As String
        Public Property AccentColor As String

        Private _lockerController As LockerControllerService
        Private _networkOk As Boolean = False
        Private _backendReachable As Boolean = False
        Private _networkChecked As Boolean = False
        Private _internetOk As Boolean = False
        Private _idValidated As Boolean = False
        Private _commissioningToken As String = Nothing
        Private _networkDiag As String = ""

        Private _restoreTopmostAfterSettings As Boolean = False
        Private _openingWifiSettings As Boolean = False
        Private _restoreTimer As DispatcherTimer = Nothing

        Private _pulseTimer As DispatcherTimer = Nothing
        Private _pulseTargetButton As Button = Nothing
        Private _pulseIsOn As Boolean = False
        Private _pulseNormalBrush As Brush = Nothing

        Private ReadOnly _pulseHighlightBrush As Brush =
            New SolidColorBrush(Color.FromRgb(255, 191, 0))

        Private ReadOnly _commissioningService As ICommissioningService
        Private ReadOnly _backendHealthService As IBackendHealthService

        Private ReadOnly LocalBypassCommissioningCode As String = AppSettings.TestCommissioningCode
        Private Const EnableBackendCommissioning As Boolean = False
        Private Const AllowOfflineCommissioning As Boolean = False

        Private ReadOnly Property UseCommissioningBypass As Boolean
            Get
                Return TypeOf _commissioningService Is BypassCommissioningService
            End Get
        End Property


        Public Sub New()
            InitializeComponent()

            Dim http As Net.Http.HttpClient = Nothing

            If Not String.IsNullOrWhiteSpace(AppSettings.BaseApiUrl) Then
                http = BackendHttpFactory.CreateHttpClient()
                _backendHealthService = New BackendHealthService(http)
            End If

            If EnableBackendCommissioning Then
                If http Is Nothing Then
                    Throw New InvalidOperationException("BaseApiUrl must be configured before backend commissioning can be enabled.")
                End If

                _commissioningService = New BackendCommissioningService(http)
            Else
                _commissioningService = New BypassCommissioningService(LocalBypassCommissioningCode)
            End If
        End Sub

        Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            Dim maxW = SystemParameters.WorkArea.Width
            Dim maxH = SystemParameters.WorkArea.Height

            If Me.Width > maxW Then Me.Width = maxW
            If Me.Height > maxH Then Me.Height = maxH

            Using db = DatabaseBootstrapper.BuildDbContext()
                Dim row = db.KioskState.AsNoTracking().
                    OrderByDescending(Function(x) x.LastUpdatedUtc).
                    FirstOrDefault()

                If row IsNot Nothing AndAlso row.IsCommissioned Then
                    MessageBox.Show("This kiosk is already commissioned.")
                    Application.Current.Shutdown()
                    Return
                End If
            End Using

            _networkChecked = False
            _internetOk = False
            _backendReachable = False
            _networkOk = False
            _idValidated = False
            _networkDiag = ""

            NetworkSummaryText.Text = "Network status: not checked."
            NetworksListBox.ItemsSource = Nothing
            ValidateButton.IsEnabled = False

            ShowWifiUi(False)
            UpdateStepGuidance()

            StatusText.Text = "Step 1: Check Connection. Step 2: Enter Commissioning ID and Validate."
        End Sub

        Private Async Sub CheckNetwork_Click(sender As Object, e As RoutedEventArgs)
            CheckNetworkButton.IsEnabled = False
            ValidateButton.IsEnabled = False

            _networkChecked = True
            _internetOk = False
            _backendReachable = False
            _networkOk = False
            _networkDiag = ""

            Try
                StatusText.Text = "Checking connection..."

                Dim ethernetUp = IsEthernetUp()
                Dim wifiPresent = IsWifiAdapterPresent()
                Dim wifiConnected = IsWifiConnected()
                Dim anyConnected = ethernetUp OrElse wifiConnected

                ShowWifiUi(False)
                NetworksListBox.ItemsSource = Nothing

                If anyConnected Then
                    If _backendHealthService IsNot Nothing Then
                        Dim health = Await _backendHealthService.CheckHealthAsync(CancellationToken.None)
                        _backendReachable = health.Reachable
                        _networkDiag = If(health.DiagnosticMessage, "")
                    Else
                        _backendReachable = False
                        _networkDiag = "Backend URL not configured."
                    End If

                    _internetOk = Await ProbeInternetAsync()
                Else
                    _networkDiag = "No active Ethernet or Wi-Fi connection."
                End If

                Dim ethText = If(ethernetUp, "Ethernet: Connected", "Ethernet: Not connected")

                Dim wifiText As String
                If wifiConnected Then
                    wifiText = "Wi-Fi: Connected"
                ElseIf wifiPresent Then
                    wifiText = "Wi-Fi: Available"
                Else
                    wifiText = "Wi-Fi: Not present"
                End If

                Dim backendText = If(_backendReachable, "Backend: Reachable", "Backend: Not reachable")
                Dim internetText = If(_internetOk, "Internet: OK", "Internet: Not reachable")

                _networkOk = _backendReachable OrElse AllowOfflineCommissioning OrElse UseCommissioningBypass
                ValidateButton.IsEnabled = _networkOk

                If Not anyConnected Then
                    ShowWifiUi(wifiPresent)

                    If wifiPresent Then
                        Dim nets As List(Of WifiNetworkItem) = GetWifiNetworksFromNetsh()
                        NetworksListBox.ItemsSource = nets
                        If nets.Count > 0 Then NetworksListBox.SelectedIndex = 0

                        NetworkSummaryText.Text = $"{ethText}  |  Wi-Fi: Available ({nets.Count} found)  |  {backendText}  |  {internetText}"
                        StatusText.Text = "No network connection detected. Connect Ethernet or Wi-Fi, then check again."
                    Else
                        NetworkSummaryText.Text = $"{ethText}  |  {wifiText}  |  {backendText}  |  {internetText}"
                        StatusText.Text = "No network connection detected. Connect Ethernet and check again."
                    End If

                Else
                    NetworkSummaryText.Text = $"{ethText}  |  {wifiText}  |  {backendText}  |  {internetText}"

                    If _backendReachable Then
                        ShowWifiUi(False)
                        StatusText.Text = "Backend reachable. Enter Commissioning ID, then Validate."
                    Else
                        ShowWifiUi(wifiPresent)

                        If wifiPresent Then
                            Dim nets As List(Of WifiNetworkItem) = GetWifiNetworksFromNetsh()
                            NetworksListBox.ItemsSource = nets
                            If nets.Count > 0 Then NetworksListBox.SelectedIndex = 0
                        End If

                        If String.IsNullOrWhiteSpace(AppSettings.BaseApiUrl) AndAlso UseCommissioningBypass Then
                            StatusText.Text = "Backend URL is not configured, but test mode allows you to continue."
                        ElseIf UseCommissioningBypass Then
                            StatusText.Text = "Backend not reachable, but test mode allows you to continue."
                        ElseIf AllowOfflineCommissioning Then
                            StatusText.Text = "Backend not reachable, but offline commissioning is allowed."
                        Else
                            If String.IsNullOrWhiteSpace(AppSettings.BaseApiUrl) Then
                                StatusText.Text = "Backend URL is not configured. Configure the backend address, then check again."
                            ElseIf Not String.IsNullOrWhiteSpace(_networkDiag) Then
                                StatusText.Text = $"Backend is not reachable. {_networkDiag}"
                            ElseIf Not _internetOk Then
                                StatusText.Text = "Network connection detected, but backend is not reachable. Verify backend address, server availability, and connectivity, then check again."
                            Else
                                StatusText.Text = "Backend is not reachable. Verify backend address and server availability, then check again."
                            End If
                        End If
                    End If
                End If

            Catch ex As Exception
                NetworkSummaryText.Text = "Network status: error."
                NetworksListBox.ItemsSource = Nothing
                ShowWifiUi(False)

                _backendReachable = False
                _networkOk = AllowOfflineCommissioning OrElse UseCommissioningBypass
                ValidateButton.IsEnabled = _networkOk
                _networkDiag = ex.Message

                StatusText.Text = "Connection check failed: " & ex.Message

            Finally
                CheckNetworkButton.IsEnabled = True
                UpdateStepGuidance()
            End Try
        End Sub

        Private Async Sub OpenWifiSettings_Click(sender As Object, e As RoutedEventArgs)
            If _openingWifiSettings Then Return
            _openingWifiSettings = True

            Try
                _restoreTopmostAfterSettings = Me.Topmost

                Me.Topmost = False
                Me.WindowState = WindowState.Normal

                Dim psi As New ProcessStartInfo("ms-settings:network-wifi") With {
                    .UseShellExecute = True
                }
                Process.Start(psi)

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

            StatusText.Text = $"Selected Wi-Fi network: {sel.Ssid}. Open Wi-Fi Settings to connect."
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

        Private Function IsWifiConnected() As Boolean
            Try
                For Each ni In System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    If ni.OperationalStatus <> System.Net.NetworkInformation.OperationalStatus.Up Then Continue For

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

            Dim lines = output.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)

            Dim currentSsid As String = Nothing
            Dim currentSignal As String = Nothing
            Dim currentAuth As String = Nothing

            For Each raw In lines
                Dim line = raw.Trim()

                If line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase) AndAlso line.Contains(":") Then
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

                If line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) AndAlso
                   line.Contains(":") AndAlso
                   currentSignal Is Nothing Then

                    currentSignal = line.Substring(line.IndexOf(":"c) + 1).Trim()
                    Continue For
                End If

                If line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) AndAlso
                   line.Contains(":") AndAlso
                   currentAuth Is Nothing Then

                    currentAuth = line.Substring(line.IndexOf(":"c) + 1).Trim()
                    Continue For
                End If
            Next

            If Not String.IsNullOrWhiteSpace(currentSsid) Then
                results.Add(New WifiNetworkItem With {
                    .Ssid = currentSsid,
                    .SignalText = If(currentSignal, ""),
                    .SecurityText = If(currentAuth, "")
                })
            End If

            results = results.
                Where(Function(x) Not String.IsNullOrWhiteSpace(x.Ssid)).
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

        Private Sub ShowWifiUi(show As Boolean)
            WifiSectionPanel.Visibility = If(show, Visibility.Visible, Visibility.Collapsed)
            OpenWifiSettingsButton.Visibility = If(show, Visibility.Visible, Visibility.Collapsed)

            If Not show Then
                NetworksListBox.ItemsSource = Nothing
            End If
        End Sub

        Private Sub Window_Activated(sender As Object, e As EventArgs) Handles Me.Activated
            If Me.WindowState = WindowState.Minimized Then
                Me.WindowState = WindowState.Normal
            End If

            If _restoreTopmostAfterSettings Then
                Me.Topmost = True
                _restoreTopmostAfterSettings = False
            End If

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
                    If Me.WindowState <> WindowState.Minimized Then
                        _restoreTimer.Stop()
                        _restoreTimer = Nothing
                        Return
                    End If

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

                StatusText.Text = "Back from Wi-Fi Settings. Press Check Connection."
            Catch
            End Try
        End Sub

        Private Sub Window_Deactivated(sender As Object, e As EventArgs) Handles Me.Deactivated
            If WifiSectionPanel.Visibility = Visibility.Visible Then
                StatusText.Text = "Finish network setup, then return here and press Check Connection."
            End If
        End Sub

        Private Async Sub Validate_Click(sender As Object, e As RoutedEventArgs)
            If Not _networkChecked AndAlso Not AllowOfflineCommissioning Then
                StatusText.Text = "Please run Check Connection first."
                Return
            End If

            If (Not _networkOk) AndAlso Not AllowOfflineCommissioning Then
                StatusText.Text = "Connection not ready. Correct the network issue, then press Check Connection again."
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
            StopButtonPulse()

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
                UpdateStepGuidance()
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

            Try
                StatusText.Text = "Opening Controller Setup..."

                Dim portsWin As New ControllerSetupWindow() With {
                    .ActorId = effectiveActorId,
                    .IsCommissioningMode = True
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
                    .backendReachable = _backendReachable
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

            StopButtonPulse()
            StatusText.Text = "Commissioning finalized successfully."
            Me.Close()
        End Sub

        Private Sub Cancel_Click(sender As Object, e As RoutedEventArgs)
            StopButtonPulse()
            Me.Close()
        End Sub

        Private Sub StartButtonPulse(target As Button)
            If target Is Nothing Then Return

            If Object.ReferenceEquals(_pulseTargetButton, target) AndAlso _pulseTimer IsNot Nothing Then
                Return
            End If

            StopButtonPulse()

            _pulseTargetButton = target
            _pulseNormalBrush = target.Background
            _pulseIsOn = False

            _pulseTimer = New DispatcherTimer()
            _pulseTimer.Interval = TimeSpan.FromMilliseconds(750)

            AddHandler _pulseTimer.Tick,
                Sub()
                    If _pulseTargetButton Is Nothing Then Return

                    _pulseIsOn = Not _pulseIsOn
                    _pulseTargetButton.Background = If(_pulseIsOn, _pulseHighlightBrush, _pulseNormalBrush)
                End Sub

            _pulseTimer.Start()
        End Sub

        Private Sub StopButtonPulse()
            If _pulseTimer IsNot Nothing Then
                _pulseTimer.Stop()
                _pulseTimer = Nothing
            End If

            If _pulseTargetButton IsNot Nothing AndAlso _pulseNormalBrush IsNot Nothing Then
                _pulseTargetButton.Background = _pulseNormalBrush
            End If

            _pulseTargetButton = Nothing
            _pulseNormalBrush = Nothing
            _pulseIsOn = False
        End Sub

        Private Sub UpdateStepGuidance()
            If Not _networkOk Then
                StartButtonPulse(CheckNetworkButton)
            ElseIf Not _idValidated Then
                StartButtonPulse(ValidateButton)
            Else
                StopButtonPulse()
            End If
        End Sub

    End Class

End Namespace
