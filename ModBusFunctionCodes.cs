using System;
namespace zModBusTCPServer
{
    public enum ModBusFunctionCodes : byte
    {
        ReadHoldingRegs = 3,
        ReadInputRegs = 4,
        WriteSingleReg = 6,
        WriteMultipleHoldingRegs = 16
    }
}
