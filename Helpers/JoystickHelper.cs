using SharpDX.DirectInput;

namespace SteamOSConfigurator.Helpers
{
    /// <summary>
    /// Métodos compartidos para lectura de estado de joystick.
    /// </summary>
    public static class JoystickHelper
    {
        /// <summary>
        /// Obtiene el valor entero de un eje del joystick por nombre.
        /// </summary>
        public static int ObtenerValorEje(JoystickState st, string eje) => eje switch
        {
            "X" => st.X,
            "Y" => st.Y,
            "Z" => st.Z,
            "RotationX" => st.RotationX,
            "RotationY" => st.RotationY,
            "RotationZ" => st.RotationZ,
            "Slider0" => st.Sliders.Length > 0 ? st.Sliders[0] : 32767,
            "Slider1" => st.Sliders.Length > 1 ? st.Sliders[1] : 32767,
            _ => 32767
        };
    }
}
