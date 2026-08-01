using System;
using System.Runtime.InteropServices;

namespace SteamOSConfigurator.Helpers
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FsrEasuConstants
    {
        public uint Const0_X, Const0_Y, Const0_Z, Const0_W;
        public uint Const1_X, Const1_Y, Const1_Z, Const1_W;
        public uint Const2_X, Const2_Y, Const2_Z, Const2_W;
        public uint Const3_X, Const3_Y, Const3_Z, Const3_W;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FsrRcasConstants
    {
        public uint Const0_X, Const0_Y, Const0_Z, Const0_W;
    }

    public static class FsrConstants
    {
        public static FsrEasuConstants CalculateEasu(
            float viewportWidth, float viewportHeight,
            float sourceWidth, float sourceHeight,
            float displayWidth, float displayHeight)
        {
            FsrEasuConstants con = new FsrEasuConstants();

            // Port of FsrEasuCon from ffx_fsr1.h
            // Output 4 constant vectors
            con.Const0_X = BitConverter.SingleToUInt32Bits(viewportWidth / displayWidth);
            con.Const0_Y = BitConverter.SingleToUInt32Bits(viewportHeight / displayHeight);
            con.Const0_Z = BitConverter.SingleToUInt32Bits(0.5f * viewportWidth / displayWidth - 0.5f);
            con.Const0_W = BitConverter.SingleToUInt32Bits(0.5f * viewportHeight / displayHeight - 0.5f);

            con.Const1_X = BitConverter.SingleToUInt32Bits(1.0f / sourceWidth);
            con.Const1_Y = BitConverter.SingleToUInt32Bits(1.0f / sourceHeight);
            con.Const1_Z = BitConverter.SingleToUInt32Bits(1.0f / sourceWidth);
            con.Const1_W = BitConverter.SingleToUInt32Bits(-1.0f / sourceHeight);

            con.Const2_X = BitConverter.SingleToUInt32Bits(-1.0f / sourceWidth);
            con.Const2_Y = BitConverter.SingleToUInt32Bits(2.0f / sourceHeight);
            con.Const2_Z = BitConverter.SingleToUInt32Bits(1.0f / sourceWidth);
            con.Const2_W = BitConverter.SingleToUInt32Bits(2.0f / sourceHeight);

            con.Const3_X = BitConverter.SingleToUInt32Bits(0.0f / sourceWidth);
            con.Const3_Y = BitConverter.SingleToUInt32Bits(4.0f / sourceHeight);
            con.Const3_Z = 0;
            con.Const3_W = 0;

            return con;
        }

        public static FsrRcasConstants CalculateRcas(float sharpness)
        {
            FsrRcasConstants con = new FsrRcasConstants();
            // FsrRcasCon port:
            // sharpness is from 0 (sharpest) to 2 (softest), but UI might use 0 (off) to 1 (max).
            // Let's assume sharpness passed in is 0.0 (sharpest) to 2.0 (softest).
            // Actually, in UI, sharpness is 0 to 1. FSR expects attenuation from 0.0 to 2.0 (higher = softer).
            // Typically: float attenuation = exp2(-sharpness * maxAttenuation) or similar.
            // But FSR 1 RCAS recommends sharpness = 0.0 to 2.0.
            // Let's use it as attenuation directly if not using stops.
            // con0[0] = (uint32_t)(exp2(-sharpness)); 
            // Wait, RCAS constant 0 is exp2(-sharpness)
            float attenuation = (float)Math.Exp(-sharpness * Math.Log(2.0));
            con.Const0_X = BitConverter.SingleToUInt32Bits(attenuation);
            con.Const0_Y = 0;
            con.Const0_Z = 0;
            con.Const0_W = 0;

            return con;
        }
    }
}
