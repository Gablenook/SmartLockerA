
Imports System
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Linq
Imports System.Windows
Imports System.Windows.Threading
Imports System.Windows.Media.Imaging
Imports System.Windows.Interop   ' Imaging.CreateBitmapSourceFromHBitmap
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports AForge.Video
Imports AForge.Video.DirectShow
Imports ZXing
Imports ZXing.Windows.Compatibility


Namespace SmartLockerKiosk
    Partial Public Class CameraScannerWindow
        Inherits Window

        Private scanTimeout As DispatcherTimer
        Private videoSource As VideoCaptureDevice
        Private processing As Integer = 0  ' 0 = idle, 1 = processing
        Private decoded As Integer = 0

        Public Property ScannedCode As String

        ' Class-level (top of CameraScannerWindow)
        Private ReadOnly zxingOptions As New ZXing.Common.DecodingOptions With {
    .TryHarder = True,
    .PossibleFormats = New List(Of ZXing.BarcodeFormat) From {
        ZXing.BarcodeFormat.CODE_128,
        ZXing.BarcodeFormat.CODE_39,
        ZXing.BarcodeFormat.EAN_13,
        ZXing.BarcodeFormat.EAN_8,
        ZXing.BarcodeFormat.UPC_A,
        ZXing.BarcodeFormat.QR_CODE
    }
}
        Private ReadOnly zxingReader As New ZXing.Windows.Compatibility.BarcodeReader() With {
    .AutoRotate = True,
    .TryInverted = True
}


        Public Sub New(left As Double, top As Double, width As Double, height As Double)
            InitializeComponent()
            Me.WindowStyle = WindowStyle.None
            Me.ResizeMode = ResizeMode.NoResize
            Me.Topmost = True
            Me.Left = left
            Me.Top = top
            Me.Width = width
            Me.Height = height
        End Sub
        Private Sub CameraScannerWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            zxingReader.Options = zxingOptions  ' attach options at runtime
            StartCamera()
            scanTimeout = New DispatcherTimer() With {.Interval = TimeSpan.FromSeconds(15)}
            AddHandler scanTimeout.Tick, AddressOf TimeoutReached
            scanTimeout.Start()
        End Sub
        Private Sub StartCamera()
            Dim cams = New FilterInfoCollection(FilterCategory.VideoInputDevice)
            If cams.Count = 0 Then
                MessageBox.Show("No camera found.")
                DialogResult = False
                Close()
                Return
            End If

            videoSource = New VideoCaptureDevice(cams(0).MonikerString)

            ' Choose the highest available mode (or prefer 1280×720 if present)
            Dim best = videoSource.VideoCapabilities _
        .OrderByDescending(Function(c) c.FrameSize.Width * c.FrameSize.Height) _
        .FirstOrDefault()
            If best IsNot Nothing Then
                videoSource.VideoResolution = best
            End If

            AddHandler videoSource.NewFrame, AddressOf OnNewFrameSafe
            videoSource.Start()
        End Sub
        Private Sub OnNewFrameSafe(sender As Object, eventArgs As NewFrameEventArgs)
            If Interlocked.Exchange(processing, 1) = 1 Then Return
            Try
                If Interlocked.CompareExchange(decoded, 0, 0) = 1 Then Return

                Using frame As System.Drawing.Bitmap = CType(eventArgs.Frame.Clone(), System.Drawing.Bitmap)
                    ' PREVIEW (full-res)
                    Dim hBitmap As IntPtr = frame.GetHbitmap()
                    Try
                        Dim src As BitmapSource =
                    System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions())
                        src.Freeze()
                        Dispatcher.BeginInvoke(Sub() CameraPreview.Source = src)
                    Finally
                        DeleteObject(hBitmap)
                    End Try

                    ' DECODE: scale and force 24bpp for ZXing
                    Using scaled As System.Drawing.Bitmap = ScaleToWidth(frame, 1000) ' try 1200 if codes are tiny
                        Using rgb As New System.Drawing.Bitmap(scaled.Width, scaled.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb)
                            Using g = System.Drawing.Graphics.FromImage(rgb)
                                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic
                                g.DrawImage(scaled, 0, 0, rgb.Width, rgb.Height)
                            End Using

                            Dim result = zxingReader.Decode(rgb)  ' decode whole frame first
                            If result IsNot Nothing Then
                                If Interlocked.Exchange(decoded, 1) = 0 Then
                                    Dim text = result.Text
                                    Dispatcher.BeginInvoke(Sub()
                                                               scanTimeout.Stop()
                                                               ScannedCode = text
                                                               StopCamera()
                                                               DialogResult = True
                                                               Close()
                                                           End Sub)
                                End If
                            End If
                        End Using
                    End Using
                End Using
            Catch
                ' ignore per-frame errors
            Finally
                Interlocked.Exchange(processing, 0)
            End Try
        End Sub
        Private Function ScaleToWidth(src As System.Drawing.Bitmap, targetWidth As Integer) As System.Drawing.Bitmap
            If src.Width <= targetWidth Then Return CType(src.Clone(), System.Drawing.Bitmap)
            Dim targetHeight = CInt(src.Height * (targetWidth / CDbl(src.Width)))
            Dim dst = New System.Drawing.Bitmap(targetWidth, targetHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb)
            Using g = System.Drawing.Graphics.FromImage(dst)
                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic
                g.DrawImage(src, 0, 0, targetWidth, targetHeight)
            End Using
            Return dst
        End Function
        Private Sub StopCamera()
            Try
                If videoSource IsNot Nothing Then
                    RemoveHandler videoSource.NewFrame, AddressOf OnNewFrameSafe
                    If videoSource.IsRunning Then
                        videoSource.SignalToStop()
                        videoSource.WaitForStop()
                    End If
                End If
            Catch
                ' ignore
            Finally
                videoSource = Nothing
            End Try
        End Sub
        Protected Overrides Sub OnClosed(e As EventArgs)
            MyBase.OnClosed(e)
            StopCamera()
        End Sub
        Private Sub TimeoutReached(sender As Object, e As EventArgs)
            scanTimeout.Stop()
            StopCamera()
            MessageBox.Show("Scan timed out.", "Timeout", MessageBoxButton.OK, MessageBoxImage.Warning)
            DialogResult = False
            Me.Close()
        End Sub
        Private Sub CancelButton_Click(sender As Object, e As RoutedEventArgs)
            scanTimeout.Stop()
            StopCamera()
            DialogResult = False
            Me.Close()
        End Sub

        <DllImport("gdi32.dll")>
        Private Shared Function DeleteObject(hObject As IntPtr) As Boolean
        End Function
    End Class
End Namespace
