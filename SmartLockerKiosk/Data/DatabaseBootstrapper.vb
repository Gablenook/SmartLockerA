Imports System.IO
Imports Microsoft.EntityFrameworkCore
Imports System.Diagnostics

Public Module DatabaseBootstrapper

    Private Const DbFolder As String = "C:\ProgramData\SmartLockerKiosk"
    Private Const DbFile As String = "smartlocker.db"

    Public Function BuildDbContext() As KioskDbContext
        Directory.CreateDirectory(DbFolder)
        Dim dbPath = Path.Combine(DbFolder, DbFile)

        Debug.WriteLine($"DB CONTEXT PATH = {dbPath}")

        Dim options = New DbContextOptionsBuilder(Of KioskDbContext)().
            UseSqlite($"Data Source={dbPath}").
            Options

        Return New KioskDbContext(options)
    End Function
    Public Sub InitializeDatabase()
        Using db = BuildDbContext()
            db.Database.EnsureCreated()

            SeedLockerSizesIfMissing(db)
            SeedLockerStatusesIfMissing(db)

            ' NEW: status rows for each locker
            SeedLockerStatusesIfMissing(db)
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

        ' If there are no lockers configured yet, nothing to do
        If Not db.Lockers.Any() Then Return

        ' Get locker ids and existing status ids
        Dim lockerIds = db.Lockers.Select(Function(l) l.LockerId).ToList()
        Dim existingStatusIds = db.LockerStatuses.Select(Function(s) s.LockerId).ToHashSet()

        Dim nowUtc = DateTime.UtcNow
        Dim added As Integer = 0

        For Each id In lockerIds
            If Not existingStatusIds.Contains(id) Then
                db.LockerStatuses.Add(New LockerStatus With {
                .LockerId = id,
                .LockState = LockState.Unknown,
                .OccupancyState = OccupancyState.Unknown, ' or Available/Vacant if you prefer
                .LastUpdatedUtc = nowUtc
            })
                added += 1
            End If
        Next

        If added > 0 Then
            db.SaveChanges()
        End If

    End Sub


End Module

