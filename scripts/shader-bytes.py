# SPDX-License-Identifier: AGPL-3.0-only
# Fork: derlenmiş oyun modu gölgelendiricilerini (Shaders/GameBlur.*.cso) C# dizisine çevirir: Views/GameModeShaders.cs.
#   python scripts/shader-bytes.py
import base64, pathlib

root = pathlib.Path(__file__).resolve().parent.parent / 'windows' / 'src' / 'CouchSubs.App'
shaders = root / 'Shaders'
out = root / 'Views' / 'GameModeShaders.cs'

def encoded(name):
    return base64.b64encode((shaders / name).read_bytes()).decode()

out.write_text(f'''// SPDX-License-Identifier: AGPL-3.0-only
// Otomatik üretildi: scripts/shader-bytes.py (kaynak Shaders/GameBlur.hlsl). Elle değiştirmeyin.

namespace CouchSubs.App.Views;

internal static class GameModeShaders
{{
    public static readonly byte[] BlurVertex = Convert.FromBase64String("{encoded('GameBlur.vs.cso')}");
    public static readonly byte[] BlurPixel = Convert.FromBase64String("{encoded('GameBlur.ps.cso')}");
}}
''', encoding='utf-8')
print('wrote', out)
