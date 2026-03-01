Imports System.Net
Imports System.Net.Http
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Security.Authentication
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

        'Make network connections
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

        'Validate Commissioning ID
        Private Sub Validate_Click(sender As Object, e As RoutedEventArgs)
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

            ' -----------------------------
            ' TEMP LOCAL VALIDATION (no backend yet)
            ' -----------------------------
            ' Put the real expected code in AppSettings when you’re ready.
            Dim expected As String = (If(_commissioningToken, "12345")).Trim()
            Dim bypass As String = "BYPASS-LOCAL"

            Dim ok As Boolean =
        String.Equals(id, expected, StringComparison.OrdinalIgnoreCase) OrElse
        String.Equals(id, bypass, StringComparison.OrdinalIgnoreCase)

            If Not ok Then
                StatusText.Text = "Invalid Commissioning ID."
                _idValidated = False
                Return
            End If

            _idValidated = True
            StatusText.Text = "Commissioning ID validated. Proceeding…"
            GoToControllerSetup()
        End Sub

        'Setup the usb-comports
        Private Sub GoToControllerSetup()
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
                StatusText.Text = "Opening Controller Setup…"

                Dim portsWin As New ControllerSetupWindow() With {.ActorId = effectiveActorId}
                portsWin.Owner = Me
                portsWin.WindowStartupLocation = WindowStartupLocation.CenterOwner
                portsWin.Topmost = True
                Dim ok = portsWin.ShowDialog()
                If ok <> True Then
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
            Dim m As New LockerCommissioningWindow(_lockerController) With {
        .ActorId = effectiveActorId,
        .KioskId = KioskId
    }
            m.Owner = Me
            m.WindowStartupLocation = WindowStartupLocation.CenterOwner
            m.ShowDialog()

            Me.Close()
        End Sub

        Private Sub Cancel_Click(sender As Object, e As RoutedEventArgs)
            Me.Close()
        End Sub

    End Class
End Namespace
