# Historia del Proyecto (WindowsLikeSteamOS)

Este archivo sirve como un registro histórico de los hitos alcanzados y las características clave que ya están funcionando perfectamente. 
**Regla de oro:** Siempre revisar este archivo antes de implementar cambios drásticos para asegurarnos de no romper lo que ya funciona.

## 🏆 Hitos Alcanzados y Componentes Funcionales

### 1. Sistema de Inyección OSD (RivaTunerCore)
* **Estado:** Funcional y Optimizado.
* **Detalles:** 
  * Integración exitosa con RTSSSharedMemoryV2.
  * Inyección directa en los juegos detectados a pantalla completa.
  * **Prevención de regresión:** El hook ha sido optimizado y reducido al mínimo (`InjectionDelay=0`). RTSS ya no causa tirones ni ralentizaciones al conectar con el juego o al abrir el panel.

### 2. Detección de Hardware y Métricas (Telemetría)
* **Estado:** Funcional (vía OSD).
* **Detalles:**
  * Valores de uso de RAM y VRAM precisos.
  * Lectura de Energía de la GPU (Watts) estabilizada.
  * Lectura de RPM de los ventiladores.
  * **Prevención de regresión:** La telemetría se muestra *exclusivamente* en el OSD dentro del juego. Se eliminó la pestaña de telemetría de la ventana principal (WPF) para evitar redundancias y consumo de recursos innecesario.

### 3. Rediseño del Quick Access Menu (QAM) por Pestañas
* **Estado:** Interfaz visual base completada. Código C# reescrito.
* **Detalles:**
  * Migración de una lista desplazable larga a un sistema profesional de 5 pestañas: Configuración, Red, Rendimiento, Mando y Energía.
  * Sistema de navegación construido con contenedores ocultables (`Visibility.Collapsed` / `Visible`) en lugar de ventanas múltiples.
  * Encabezado unificado con icono del juego activo, título, hora y nivel de batería.
  * **Prevención de regresión:** Mantener la estética premium y ordenada. Todas las nuevas opciones deben integrarse dentro de su pestaña correspondiente.

### 4. Funciones de Energía (PowerService)
* **Estado:** Funcional.
* **Detalles:**
  * Uso de llamadas nativas (`P/Invoke` a `PowrProf.dll`) para las funciones de Suspender e Hibernar, sin dependencias de Windows Forms.
  * Control de Apagar y Reiniciar sin problemas.

## 🚧 Próximos Objetivos (En Desarrollo)

* **Pestaña Configuración:** Brillo, Modo Noche (Filtro de Luz Azul), Selección de Salida de Audio.
* **Pestaña Red:** Control de Wi-Fi, Bluetooth y Modo Avión.
* **Pestaña Rendimiento:** Control de TDP, Reloj de GPU, Filtros de Escalado.
* **Pestaña Mando:** Probar Mando, Apagar Mando, Interfaz de Mapeo.
