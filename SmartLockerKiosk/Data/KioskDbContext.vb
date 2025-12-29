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
        HasMaxLength(1)

        e.Property(Function(x) x.Zone).
        HasMaxLength(30)
    End Sub
    Private Shared Sub ConfigureLockerStatus(modelBuilder As ModelBuilder)
        Dim e = modelBuilder.Entity(Of LockerStatus)()

        ' LockerStatus is 1:1 with Locker, keyed by LockerId
        e.HasKey(Function(x) x.LockerId)

        ' If you have additional fields you want constrained, do them here
        ' e.Property(Function(x) x.LastUpdatedUtc).IsRequired()
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

        e.HasData(
        New LockerSize With {.SizeCode = "A", .DisplayName = "Small", .WidthIn = 10D, .HeightIn = 6D, .DepthIn = 14D, .SortOrder = 1, .IsEnabled = True},
        New LockerSize With {.SizeCode = "B", .DisplayName = "Medium", .WidthIn = 12D, .HeightIn = 10D, .DepthIn = 16D, .SortOrder = 2, .IsEnabled = True},
        New LockerSize With {.SizeCode = "C", .DisplayName = "Large", .WidthIn = 15D, .HeightIn = 12D, .DepthIn = 18D, .SortOrder = 3, .IsEnabled = True},
        New LockerSize With {.SizeCode = "D", .DisplayName = "XL", .WidthIn = 18D, .HeightIn = 15D, .DepthIn = 20D, .SortOrder = 4, .IsEnabled = True},
        New LockerSize With {.SizeCode = "E", .DisplayName = "Oversize", .WidthIn = 24D, .HeightIn = 18D, .DepthIn = 24D, .SortOrder = 5, .IsEnabled = True}
    )

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

