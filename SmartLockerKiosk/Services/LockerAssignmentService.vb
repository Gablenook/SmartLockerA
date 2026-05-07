Imports System.Linq
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Public Class LockerAssignmentService
    Public Function SelectNextAvailableLockerNumber(sizeCode As String, Optional preferredZone As String = Nothing) As String
        Dim code = (If(sizeCode, "")).Trim().ToUpperInvariant()
        If code.Length = 0 Then Return ""

        Dim nowUtc = DateTime.UtcNow

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim candidates = db.Lockers.
            AsNoTracking().
            Include(Function(l) l.Status).
            Where(Function(l) l.IsEnabled).
            Where(Function(l) l.SizeCode IsNot Nothing AndAlso l.SizeCode.Trim().ToUpper() = code).
            Where(Function(l) l.Status IsNot Nothing).
            Where(Function(l) l.Status.OccupancyState = OccupancyState.Vacant).
            Where(Function(l) l.Status.LockState = LockState.Closed).
            Where(Function(l) Not l.Status.ReservedUntilUtc.HasValue OrElse l.Status.ReservedUntilUtc.Value <= nowUtc).
            Where(Function(l) Not l.Status.PackagePresent.HasValue OrElse l.Status.PackagePresent.Value = False)

            If Not String.IsNullOrWhiteSpace(preferredZone) Then
                Dim z = preferredZone.Trim()

                candidates = candidates.
                OrderByDescending(Function(l) l.Zone IsNot Nothing AndAlso l.Zone = z).
                ThenBy(Function(l) l.RelayId).
                ThenBy(Function(l) l.LockerNumber)
            Else
                candidates = candidates.
                OrderBy(Function(l) l.RelayId).
                ThenBy(Function(l) l.LockerNumber)
            End If

            Dim locker = candidates.FirstOrDefault()
            If locker Is Nothing Then Return ""

            Return (If(locker.LockerNumber, "")).Trim()
        End Using
    End Function
    Public Function SelectNextOccupiedLockerNumberByDeviceType(deviceType As String,
                                                           Optional preferredZone As String = Nothing) As String
        Dim requestedType = (If(deviceType, "")).Trim()
        If requestedType.Length = 0 Then Return ""

        Dim nowUtc = DateTime.UtcNow

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim candidates = db.Lockers.
                AsNoTracking().
                Include(Function(l) l.Status).
                Where(Function(l) l.IsEnabled).
                Where(Function(l) l.Status IsNot Nothing).
                Where(Function(l) l.Status.OccupancyState = OccupancyState.Occupied).
                Where(Function(l) l.Status.LockState = LockState.Closed).
                Where(Function(l) Not l.Status.ReservedUntilUtc.HasValue OrElse l.Status.ReservedUntilUtc.Value <= nowUtc).
                Where(Function(l) Not l.Status.IsDefectiveHold).
                Where(Function(l) l.Status.CurrentDeviceType IsNot Nothing AndAlso
                                  l.Status.CurrentDeviceType.Trim().ToUpper() = requestedType.ToUpper())

            If Not String.IsNullOrWhiteSpace(preferredZone) Then
                Dim z = preferredZone.Trim()

                candidates = candidates.
                    OrderByDescending(Function(l) l.Zone IsNot Nothing AndAlso l.Zone = z).
                    ThenBy(Function(l) l.RelayId).
                    ThenBy(Function(l) l.LockerNumber)
            Else
                candidates = candidates.
                    OrderBy(Function(l) l.RelayId).
                    ThenBy(Function(l) l.LockerNumber)
            End If

            Dim locker = candidates.FirstOrDefault()
            If locker Is Nothing Then Return ""

            Return (If(locker.LockerNumber, "")).Trim()
        End Using
    End Function
    Public Function SelectNextAvailableAssetLockerNumber(
    Optional preferredZone As String = Nothing
) As String

        Using db = DatabaseBootstrapper.BuildDbContext()

            Dim nowUtc As DateTime = DateTime.UtcNow
            Dim zoneFilter As String = If(preferredZone, String.Empty).Trim()

            TraceLogger.Log("ASSET LOCKER SELECT DB=" & db.Database.GetDbConnection().DataSource)
            TraceLogger.Log($"ASSET LOCKER SELECT preferredZone='{zoneFilter}'")
            TraceLogger.Log($"ENUMS Vacant={CInt(OccupancyState.Vacant)} Closed={CInt(LockState.Closed)}")

            Dim lockers As List(Of Locker)

            Try
                lockers = db.Lockers.
                Include(Function(l) l.Status).
                AsNoTracking().
                ToList()

            Catch ex As Exception
                TraceLogger.LogExceptionDeep("ASSET_LOCKER_SELECT_LOAD_FAIL", ex)
                Throw
            End Try

            TraceLogger.Log($"ASSET LOCKER RAW COUNT={lockers.Count}")

            Dim candidates As New List(Of Locker)

            For Each locker In lockers

                Dim status = locker.Status
                Dim rejectReason As String = Nothing

                If Not locker.IsEnabled Then
                    rejectReason = "IsEnabled=False"

                ElseIf status Is Nothing Then
                    rejectReason = "Status is Nothing"

                ElseIf status.OccupancyState <> OccupancyState.Vacant Then
                    rejectReason = $"OccupancyState={CInt(status.OccupancyState)}"

                ElseIf status.LockState <> LockState.Closed Then
                    rejectReason = $"LockState={CInt(status.LockState)}"

                ElseIf status.PackagePresent.HasValue AndAlso status.PackagePresent.Value Then
                    rejectReason = "PackagePresent=True"

                ElseIf status.ReservedUntilUtc.HasValue AndAlso status.ReservedUntilUtc.Value > nowUtc Then
                    rejectReason = $"ReservedUntilUtc={status.ReservedUntilUtc.Value:O}"

                ElseIf zoneFilter.Length > 0 AndAlso
                   Not String.Equals(If(locker.Zone, String.Empty).Trim(),
                                     zoneFilter,
                                     StringComparison.OrdinalIgnoreCase) Then
                    rejectReason = $"Zone mismatch. LockerZone='{locker.Zone}'"
                End If

                Dim reservedText As String = "NULL"

                If status IsNot Nothing AndAlso status.ReservedUntilUtc.HasValue Then
                    reservedText = status.ReservedUntilUtc.Value.ToString("O")
                End If

                If rejectReason Is Nothing Then
                    candidates.Add(locker)

                    TraceLogger.Log(
                    $"ASSET LOCKER ACCEPT " &
                    $"Locker={locker.LockerNumber}; " &
                    $"Relay={locker.RelayId}; " &
                    $"Zone={locker.Zone}; " &
                    $"ReservedUntil={reservedText}")
                Else
                    TraceLogger.Log(
                    $"ASSET LOCKER REJECT " &
                    $"Locker={locker.LockerNumber}; " &
                    $"Enabled={locker.IsEnabled}; " &
                    $"Relay={locker.RelayId}; " &
                    $"Zone={locker.Zone}; " &
                    $"Reason={rejectReason}; " &
                    $"StatusExists={status IsNot Nothing}; " &
                    $"LockState={If(status Is Nothing, -1, CInt(status.LockState))}; " &
                    $"Occupancy={If(status Is Nothing, -1, CInt(status.OccupancyState))}; " &
                    $"PackagePresent={If(status Is Nothing, "NULL", status.PackagePresent.ToString())}; " &
                    $"ReservedUntil={reservedText}")
                End If

            Next

            TraceLogger.Log($"ASSET LOCKER SELECT candidate count={candidates.Count}")

            Dim selected = candidates.
            OrderBy(Function(l) l.RelayId).
            ThenBy(Function(l) l.LockerNumber).
            FirstOrDefault()

            If selected Is Nothing Then
                TraceLogger.Log("ASSET LOCKER SELECT result=None")
                Return Nothing
            End If

            TraceLogger.Log($"ASSET LOCKER SELECT result={selected.LockerNumber}")

            Return selected.LockerNumber

        End Using

    End Function
End Class
