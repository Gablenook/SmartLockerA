Imports Microsoft.EntityFrameworkCore

Public Class KioskDbContext
    Inherits DbContext

    Public Sub New(options As DbContextOptions(Of KioskDbContext))
        MyBase.New(options)
    End Sub

    Public Property Lockers As DbSet(Of Locker)
    Public Property LockerStatuses As DbSet(Of LockerStatus)
    Public Property ControllerPorts As DbSet(Of ControllerPort)
    Public Property LockerSizes As DbSet(Of LockerSize)

    Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder)
        MyBase.OnModelCreating(modelBuilder)
        ConfigureLocker(modelBuilder)
        ConfigureLockerStatus(modelBuilder)
        ConfigureLockerToStatusRelationship(modelBuilder)
        ConfigureControllerPort(modelBuilder)
        ConfigureLockerSize(modelBuilder)
        ConfigureLockerToSizeRelationship(modelBuilder)
    End Sub
    Private Shared Sub ConfigureLocker(modelBuilder As ModelBuilder)
        Dim e = modelBuilder.Entity(Of Locker)()

        ' Primary key
        e.HasKey(Function(x) x.LockerId)

        ' Branch is required and is a single-character code ("A"/"B")
        e.Property(Function(x) x.Branch).
        IsRequired().
        HasMaxLength(1)

        ' RelayId is required
        e.Property(Function(x) x.RelayId).
        IsRequired()

        ' LockerNumber: alphanumeric, required, unique
        e.Property(Function(x) x.LockerNumber).
        IsRequired().
        HasMaxLength(30)

        e.HasIndex(Function(x) x.LockerNumber).
        IsUnique()

        ' Physical address must also be unique
        e.HasIndex(Function(x) New With {x.Branch, x.RelayId}).
        IsUnique()

        ' Optional fields
        e.Property(Function(x) x.SizeCode).
        HasMaxLength(10)

        e.Property(Function(x) x.Zone).
        HasMaxLength(30)
    End Sub
    Private Sub ConfigureLockerStatus(modelBuilder As ModelBuilder)

        modelBuilder.Entity(Of LockerStatus)(
        Sub(e)

            ' PK
            e.HasKey(Function(x) x.LockerId)

            ' Enums stored as integers (default, but explicit is nice)
            e.Property(Function(x) x.LockState).HasConversion(Of Integer)()
            e.Property(Function(x) x.OccupancyState).HasConversion(Of Integer)()

            ' Optional flags
            e.Property(Function(x) x.PackagePresent).IsRequired(False)

            ' Timestamps
            e.Property(Function(x) x.LastUpdatedUtc).IsRequired()

            ' If you add these fields:
            e.Property(Function(x) x.ReservedUntilUtc).IsRequired(False)

            e.Property(Function(x) x.ReservedCorrelationId).
                HasMaxLength(64).
                IsRequired(False)

            e.Property(Function(x) x.ReservedWorkOrderNumber).
                HasMaxLength(64).
                IsRequired(False)

            e.Property(Function(x) x.LastWorkOrderNumber).
                HasMaxLength(64).
                IsRequired(False)

            e.Property(Function(x) x.LastActorId).
                HasMaxLength(64).
                IsRequired(False)

            e.Property(Function(x) x.LastReason).
                HasMaxLength(64).
                IsRequired(False)

            ' Helpful indexes for fast assignment queries
            e.HasIndex(Function(x) x.OccupancyState)
            e.HasIndex(Function(x) x.ReservedUntilUtc)

        End Sub)

    End Sub
    Private Shared Sub ConfigureLockerToStatusRelationship(modelBuilder As ModelBuilder)
        modelBuilder.Entity(Of Locker)().
            HasOne(Function(l) l.Status).
            WithOne(Function(s) s.Locker).
            HasForeignKey(Of LockerStatus)(Function(s) s.LockerId).
            OnDelete(DeleteBehavior.Cascade)
    End Sub
    Private Shared Sub ConfigureControllerPort(modelBuilder As ModelBuilder)
        Dim e = modelBuilder.Entity(Of ControllerPort)()

        e.HasKey(Function(x) x.BranchName)

        e.Property(Function(x) x.BranchName).
            IsRequired().
            HasMaxLength(1)

        e.Property(Function(x) x.PortName).
            HasMaxLength(20)
    End Sub
    Private Shared Sub ConfigureLockerSize(modelBuilder As ModelBuilder)
        Dim e = modelBuilder.Entity(Of LockerSize)()

        e.HasKey(Function(x) x.SizeCode)

        e.Property(Function(x) x.SizeCode).
        IsRequired().
        HasMaxLength(10) ' <-- allow custom codes like "XL", "S1", etc.

        e.Property(Function(x) x.DisplayName).
        HasMaxLength(40)

        e.Property(Function(x) x.WidthIn).IsRequired()
        e.Property(Function(x) x.HeightIn).IsRequired()
        e.Property(Function(x) x.DepthIn).IsRequired()

        e.Property(Function(x) x.SortOrder).IsRequired()
        e.Property(Function(x) x.IsEnabled).IsRequired()

        ' Helpful index for ordering enabled sizes
        e.HasIndex(Function(x) New With {x.IsEnabled, x.SortOrder})
    End Sub
    Private Shared Sub ConfigureLockerToSizeRelationship(modelBuilder As ModelBuilder)
        ' Locker.SizeCode -> LockerSize.SizeCode (optional relationship)
        modelBuilder.Entity(Of Locker)().
        HasOne(Function(l) l.Size).
        WithMany().
        HasForeignKey(Function(l) l.SizeCode).
        HasPrincipalKey(Function(s) s.SizeCode).
        OnDelete(DeleteBehavior.Restrict) ' don't delete sizes if lockers exist
    End Sub

End Class

