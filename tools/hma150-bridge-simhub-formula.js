// SimHub Custom Serial Device -> "Update messages" -> JavaScript formula.
// Sends one text line per update for hma150_bridge_v14.ps1 (-SimHubSerial).
function g(n) { var v = $prop(n); return (v === null || v === undefined || v === '') ? 0 : v; }
var gear = String(g('DataCorePlugin.GameData.Gear'));
var gn = (gear === 'N') ? 0 : (gear === 'R' ? 0 : (parseInt(gear) || 0));
return 'HMA,' +
  g('DataCorePlugin.GameData.OrientationPitch') + ',' +
  g('DataCorePlugin.GameData.OrientationRoll') + ',' +
  g('DataCorePlugin.GameData.OrientationYaw') + ',' +
  g('DataCorePlugin.GameData.OrientationYawVelocity') + ',' +
  g('DataCorePlugin.GameData.AccelerationSurge') + ',' +
  g('DataCorePlugin.GameData.AccelerationSway') + ',' +
  g('DataCorePlugin.GameData.AccelerationHeave') + ',' +
  g('DataCorePlugin.GameData.SpeedKmh') + ',' +
  g('DataCorePlugin.GameData.Brake') + ',' +
  g('DataCorePlugin.GameData.Throttle') + ',' +
  gn + ',' + Date.now() + '\n';
