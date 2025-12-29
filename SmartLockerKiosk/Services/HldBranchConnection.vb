Imports HldSerialLib.Serial.LockBoard

Namespace SmartLockerKiosk

    Public Class HldBranchConnection
        Public ReadOnly Property PortName As String
        Public ReadOnly Property IsConnected As Boolean

        Private _board As HldLockBoard

        Public Sub New(portName As String)
            Me.PortName = portName
        End Sub

        Public Sub Connect()
            If _board IsNot Nothing Then Return

            _board = New HldLockBoard()
            _board.Open(PortName, 115200) ' SDK sets rest internally; if not, vendor usually still uses 8N1
        End Sub

        Public Sub Disconnect()
            If _board Is Nothing Then Return
            Try
                _board.Close()
            Catch
            End Try
            _board = Nothing
        End Sub

        Public Function TryPing(Optional relayId As Integer = 1) As Boolean
            Try
                Dim v = _board.GetLockStatus(relayId)
                Return (v = 0 OrElse v = 1)
            Catch
                Return False
            End Try
        End Function

        Public Function GetLockStatus(relayId As Integer) As Integer
            Return _board.GetLockStatus(relayId)
        End Function

        Public Function GetSensorStatus(relayId As Integer) As Integer
            Return _board.GetSensorStatus(relayId)
        End Function

        Public Sub Unlock(relayId As Integer)
            _board.Unlock(relayId)
        End Sub

    End Class

End Namespace

