## EEPROM direct access (group `0x0A` / 10 — any device)

Low-level EEPROM read/write protocol, applicable to any device. Bypasses the named command interface. Found in `rs21_parameter.db` but not observed in USB captures.

| Command | ID | Dir | Bytes | Type | Notes |
|---------|----|-----|-------|------|-------|
| select-table | `00 05` | W | 4 | int | Select EEPROM table ID |
| read-table | `00 06` | R | 4 | int | Read selected table ID |
| select-address | `00 07` | W | 4 | int | Select address within table |
| read-address | `00 08` | R | 4 | int | Read selected address |
| write-int | `00 09` | W | 4 | int | Write int at selected table+address |
| read-int | `00 0A` | R | 4 | int | Read int at selected table+address |
| write-float | `00 0B` | W | 4 | float | Write float at selected table+address |
| read-float | `00 0C` | R | 4 | float | Read float at selected table+address |

Known EEPROM tables:

| Table | ID | Params | Notes |
|-------|----|--------|-------|
| Base | 2 | 38 | |
| Motor | 3 | 76 | PID/encoder/field-weakening |
| Wheel | 4 | 123 | |
| Pedals | 5 | 45 | Param 6 also written by iRacing's `SetMotorRunState` CoAP probe (group `0x2C` cmd `0x01` — see [`../devices/wheelbase-0x13.md`](../devices/wheelbase-0x13.md) § Group 0x2C). The cell is dual-purpose; its pedals semantics on non-iRacing sessions are not yet decoded. |
| Unknown | 11 | ≥14 | Partner-SDK / iRacing parameters. Params 13 + 14 are written as a pair by iRacing's `HighFrequencyTorque` CoAP probe (group `0x2A` cmd `0x41` — see [`../devices/wheelbase-0x13.md`](../devices/wheelbase-0x13.md) § Group 0x2A). Firmware `[INFO]param_manage.c:340` echoes the writes verbatim on group `0x0E`. Original "8 params" count was a lower bound from earlier captures; actual extent unknown. |
