# Shizuku instantiates the user service by class name inside its own app_process (ADR-0009).
-keep class dshmobile.relay.ShellService { *; }
-keep class dshmobile.relay.ShellService$* { *; }
