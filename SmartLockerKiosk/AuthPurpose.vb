Imports System.Runtime.Serialization

Public Enum AuthPurpose
    <EnumMember(Value:="Pickup")>
    PickupAccess

    <EnumMember(Value:="Deliver")>
    DeliveryCourierAuth

    <EnumMember(Value:="Admin")>
    AdminAccess

    <EnumMember(Value:="DayUse")>
    DayUseStart
End Enum

