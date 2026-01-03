Imports System.ComponentModel
Imports System.Runtime.CompilerServices

Public Class LockerStatusRow
        Implements INotifyPropertyChanged

        Public Property LockerId As Integer
        Public Property LockerNumber As String
        Public Property Branch As String
        Public Property RelayId As Integer
        Public Property SizeCode As String

        Private _occupancyState As OccupancyState
        Public Property OccupancyState As OccupancyState
            Get
                Return _occupancyState
            End Get
            Set(value As OccupancyState)
                If _occupancyState <> value Then
                    _occupancyState = value
                    IsDirty = True
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _lockState As LockState
        Public Property LockState As LockState
            Get
                Return _lockState
            End Get
            Set(value As LockState)
                If _lockState <> value Then
                    _lockState = value
                    IsDirty = True
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _packagePresent As Boolean?
        Public Property PackagePresent As Boolean?
            Get
                Return _packagePresent
            End Get
            Set(value As Boolean?)
                If _packagePresent <> value Then
                    _packagePresent = value
                    IsDirty = True
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Private _reason As String
        Public Property Reason As String
            Get
                Return _reason
            End Get
            Set(value As String)
                If _reason <> value Then
                    _reason = value
                    IsDirty = True
                    OnPropertyChanged()
                End If
            End Set
        End Property

        Public Property ReservedUntilUtc As DateTime?
        Public Property ReservedWorkOrderNumber As String
        Public Property LastUpdatedUtc As DateTime
        Public Property LastActorId As String

        Public Property IsDirty As Boolean = False

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        Private Sub OnPropertyChanged(<CallerMemberName> Optional name As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub
    End Class

