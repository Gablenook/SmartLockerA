Imports System.Diagnostics
Imports System.IO
Imports Microsoft.EntityFrameworkCore
Imports SmartLockerKiosk.SmartLockerKiosk

Public Module DatabaseBootstrapper

    Private Const DbFolder As String = "C:\ProgramData\SmartLockerKiosk"
    Private Const DbFile As String = "smartlocker.db"

    ' Keep this internal helper so we don't duplicate path logic everywhere.
    Private Function GetDbPath() As String
        Directory.CreateDirectory(DbFolder)
        Return Path.Combine(DbFolder, DbFile)
    End Function
    Public Function BuildDbContext() As KioskDbContext
        Dim dbPath = GetDbPath()


        Dim options = New DbContextOptionsBuilder(Of KioskDbContext)().
            UseSqlite($"Data Source={dbPath}").
            Options

        Return New KioskDbContext(options)
    End Function

    Public Sub InitializeDatabase()
        Using db = BuildDbContext()

            ' Since you are not using migrations yet:
            db.Database.EnsureCreated()

            ' Seed in a single place; each method is idempotent.
            SeedLockerSizesIfMissing(db)
            SeedLockerStatusesIfMissing(db)
            SeedKioskStateIfMissing(db)

        End Using
    End Sub
    Private Sub SeedLockerSizesIfMissing(db As KioskDbContext)
        If db.LockerSizes.Any() Then Return

        db.LockerSizes.AddRange(
            New LockerSize With {.SizeCode = "A", .DisplayName = "Small", .WidthIn = 10D, .HeightIn = 6D, .DepthIn = 14D, .SortOrder = 1, .IsEnabled = True},
            New LockerSize With {.SizeCode = "B", .DisplayName = "Medium", .WidthIn = 12D, .HeightIn = 10D, .DepthIn = 16D, .SortOrder = 2, .IsEnabled = True},
            New LockerSize With {.SizeCode = "C", .DisplayName = "Large", .WidthIn = 15D, .HeightIn = 12D, .DepthIn = 18D, .SortOrder = 3, .IsEnabled = True},
            New LockerSize With {.SizeCode = "D", .DisplayName = "XL", .WidthIn = 18D, .HeightIn = 15D, .DepthIn = 20D, .SortOrder = 4, .IsEnabled = True},
            New LockerSize With {.SizeCode = "E", .DisplayName = "Oversize", .WidthIn = 24D, .HeightIn = 18D, .DepthIn = 24D, .SortOrder = 5, .IsEnabled = True}
        )

        db.SaveChanges()
    End Sub
    Private Sub SeedLockerStatusesIfMissing(db As KioskDbContext)

        ' If there are no lockers configured yet, nothing to seed
        If Not db.Lockers.Any() Then Return

        Dim lockerIds As List(Of Integer) =
            db.Lockers.Select(Function(l) l.LockerId).ToList()

        Dim existingStatusIds As HashSet(Of Integer) =
            db.LockerStatuses.Select(Function(s) s.LockerId).ToHashSet()

        Dim nowUtc = DateTime.UtcNow
        Dim added As Integer = 0

        For Each id In lockerIds
            If Not existingStatusIds.Contains(id) Then
                db.LockerStatuses.Add(New LockerStatus With {
                    .LockerId = id,
                    .LockState = LockState.Unknown,
                    .OccupancyState = OccupancyState.Unknown,
                    .LastUpdatedUtc = nowUtc
                })
                added += 1
            End If
        Next

        If added > 0 Then db.SaveChanges()

    End Sub
    Private Sub SeedKioskStateIfMissing(db As KioskDbContext)
        Dim kioskId As String = (If(AppSettings.KioskID, "")).Trim()
        If kioskId.Length = 0 Then Return

        Dim row = db.KioskState.SingleOrDefault(Function(x) x.KioskId = kioskId)
        If row IsNot Nothing Then Return

        db.KioskState.Add(New KioskState With {
            .KioskId = kioskId,
            .LocationId = AppSettings.LocationId,
            .IsCommissioned = False,
            .LastUpdatedUtc = DateTime.UtcNow
       })
        db.SaveChanges()
    End Sub

End Module


