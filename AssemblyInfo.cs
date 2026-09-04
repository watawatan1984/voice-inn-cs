using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// テストプロジェクト (tests/VoiceIn.Tests) から internal メンバーへアクセスできるようにする。
// HistoryManager のテスト用コンストラクタや、AudioRecorder に切り出した純粋計算ロジック
// (ゲイン適用・RMS/Peak集計) を internal のまま単体テストするために必要。
// public API のサーフェスは一切広げない (テストアセンブリのみへの限定公開)。
[assembly: InternalsVisibleTo("VoiceIn.Tests")]
