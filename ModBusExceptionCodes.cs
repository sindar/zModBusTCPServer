using System;
namespace zModBusTCPServer
{
    public enum ModBusExceptionCodes : byte
    {
        IllegalFunction = 1,
        IllegalDataAddress,
        IllegalDataValue,
        SlaveDeviceFailure,
        Acknowledge,
        SlaveDeviceBusy,
        NegativeAcknowledge,
        MemoryParityError,
        GatewayPathUnavailable,
        GatewayTargetDeviceFailedToRespond
    }
}
