# WindowsLikeSteamOS

WindowsLikeSteamOS es una forma súper rápida de obtener una experiencia muy, muy similar a SteamOS, pero manteniendo la compatibilidad y las ventajas de Windows. Está pensado para ejecutar un entorno tipo "Big Picture"/consola dentro de Windows creando un usuario exclusivo para ese modo y aplicando unas optimizaciones muy especificas.

**Qué hace**
- Proporciona una interfaz tipo consola/Big Picture basada en WPF que arranca a pantalla completa.
- Mantiene compatibilidad con juegos y aplicaciones Windows sin requerir una máquina separada.

**Funcionamiento**
- Crea un usuario exclusivo (`SteamOS`) dedicado al modo consola.
- Configura ese usuario para iniciar la aplicación en modo quiosco o como shell personalizado.
- Aplica optimizaciones (plan de energía, Game Mode, priorizar GPU) para mejorar la experiencia de juego.
