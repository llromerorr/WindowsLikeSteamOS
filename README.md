# WindowsLikeSteamOS 🎮✨

Transforma tu PC con Windows en una consola de videojuegos dedicada, replicando la experiencia fluida e integrada de SteamOS (Steam Big Picture) en una cuenta de usuario aislada, con optimizaciones automáticas de hardware a bajo nivel.

Este proyecto consta de dos partes integradas en un único ejecutable autocontenido:
1. **Configurador Administrativo (GUI):** Una interfaz limpia y moderna que gestiona la instalación del entorno, la creación del perfil y la personalización de pantalla, tasa de refresco y audio.
2. **Master Shell Extensivo:** Un reemplazo directo de `explorer.exe` para el usuario dedicado, encargado de aislar el hardware para jugar sin distracciones y devolver el sistema a la normalidad al apagar.

---

## 🚀 Características Clave

* **Aislamiento Avanzado de Pantallas (Topología Win32):** Apaga físicamente y corta la señal de los monitores secundarios seleccionando un monitor gaming principal. Olvídate de que los juegos se abran en la pantalla incorrecta o de que el cursor se escape.
* **Fuerza Bruta Visual (Resolución y Hz Estrictos):** Configura la resolución nativa y la tasa de refresco exacta antes de que Steam despierte, eliminando parpadeos y desajustes.
* **Enrutamiento de Audio Exclusivo:** Cambia el dispositivo de audio por defecto (por ejemplo, hacia una TV por HDMI o una consola de audio USB) exclusivamente durante la sesión de juego.
* **Inicio de Sesión Directo (Sin Contraseña):** El instalador configura automáticamente las directivas OOBE, elimina las animaciones de bienvenida de Windows y purga las credenciales de la cuenta `SteamOS` para un arranque directo al menú de juegos.
* **Avatar en Alta Fidelidad Nativa:** Extrae de manera quirúrgica el icono maestro en resolución de 256x256 píxeles directamente desde el núcleo de tu `steam.exe` para usarlo como foto de perfil de Windows.
* **Cierre de Sesión Fulminante:** Utiliza llamadas asíncronas nativas a la API `ExitWindowsEx` para liquidar la sesión de juego en milisegundos una vez que cierras Steam.
* **Atajo de Emergencia (Modo Escritorio):** Presiona `Ctrl + Alt + Shift + S` en cualquier momento (incluso dentro de un juego) para forzar el encendido de todas tus pantallas, matar Steam y levantar el explorador clásico de Windows sin cerrar la sesión.

---

## 🛠️ Requisitos del Sistema

* **Sistema Operativo:** Windows 10 u 11 de 64 bits.
* **Privilegios:** Requiere ejecutarse obligatoriamente como **Administrador** para manipular el registro del sistema, crear cuentas locales y administrar hives de otros usuarios.
* **Dependencias de compilación:** .NET 8.0 SDK.

---

## 📦 Compilación y Despliegue (Single File Executable)

Para distribuir un único archivo `.exe` limpio, elegante y autocontenido (que incluya los motores gráficos de WPF y las dependencias nativas de audio sin requerir que el usuario instale .NET), abre tu terminal en la raíz del proyecto y ejecuta:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

El ejecutable final se generará en la ruta:  
`\bin\Release\net8.0-windows\win-x64\publish\WindowsLikeSteamOS.exe`

> ⚠️ **Nota sobre el instalador de RTSS:** El archivo `Dependencias/RTSSSetup.exe` pesa más de 18MB. Se recomienda utilizar **Git LFS** (Large File Storage) para versionar este archivo y evitar inflar el tamaño del repositorio.

---

## 🎮 Traducción de Mandos (Emulador)

La aplicación incluye un traductor nativo de mandos DirectInput a Xbox 360 usando `ViGEmBus` y `HidHide`. Esto permite:
* Soporte nativo para Steam Input de cualquier mando genérico.
* **Mapeo personalizado** mediante una interfaz interactiva de la aplicación.
* **Gatillos analógicos o digitales** soportados automáticamente.
* **Atajo Guide:** Presionar `Select + Start` se traduce en el botón Xbox Guide, permitiendo abrir el menú de Steam en juegos sin necesidad de teclado.

---

## 💻 Guía de Uso



1. Toma el `WindowsLikeSteamOS.exe` compilado y hazle **Clic derecho -> Ejecutar como Administrador**.
2. Selecciona tu pantalla gaming principal, la resolución deseada, los Hz y el dispositivo de salida de audio.
3. Haz clic en **INSTALAR ENTORNO**. La aplicación creará el usuario local `SteamOS`, configurará el Shell en el registro y extraerá el avatar en alta definición.
4. Cierra tu sesión actual de Windows.
5. Verás al nuevo usuario `SteamOS` con el logo oficial de Valve. Haz clic en él. Entrará de inmediato sin pedir contraseña.
6. **¡Listo!** El sistema apagará tus otras pantallas y abrirá Steam en modo consola frente a tus ojos. Al salir de Steam, tu PC cerrará la sesión en un parpadeo.

> 💡 **Tip de Desarrollador:** Si deseas realizar un cambio visual o de audio en el futuro, no necesitas desinstalar nada. Simplemente abre la app en tu usuario normal, cambia los selectores y dale a **APLICAR CONFIGURACIÓN**. El instalador reemplazará de forma invisible el binario en la ruta segura de Windows (`C:\ProgramData\SteamOS`) sin tocar tu cuenta.

---

## 🛑 Atajo de Emergencia del Shell

Si estás dentro del entorno de juego y necesitas acceder al escritorio de Windows de forma urgente, utiliza el comando global:
* ✨ **`Ctrl + Alt + Shift + S`**

Este Hook de bajo nivel capturará la orden, encenderá de inmediato todas tus pantallas secundarias restaurando sus posiciones originales, cerrará Steam de la memoria RAM y cargará `explorer.exe` instantáneamente.

---

## ⚖️ Licencia

Este proyecto se distribuye bajo la licencia MIT. Siéntete libre de modificarlo, adaptarlo y expandir sus capacidades gráficas y de hardware.