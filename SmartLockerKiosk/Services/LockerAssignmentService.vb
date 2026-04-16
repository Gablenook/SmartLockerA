Imports System.Linq
Imports Microsoft.EntityFrameworkCore

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
End Class
