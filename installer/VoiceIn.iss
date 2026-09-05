; Voice In インストーラー定義 (Inno Setup 6)
;
; ビルド方法: installer\build.ps1 を実行すると、
;   1) dotnet publish (自己完結型 win-x64) を dist\publish へ出力し、
;   2) 本スクリプトを ISCC.exe でコンパイルして dist\ にインストーラー exe を生成する。
; ISCC.exe を直接使い、このファイル単体をコンパイルすることもできる
; (ただしその場合は事前に dist\publish が最新の状態である必要がある)。
;
; ---------------------------------------------------------------------------
; 【データ保護に関する設計方針 (最重要)】
; ・インストール先はユーザー単位の {localappdata}\Programs\VoiceIn とし、
;   PrivilegesRequired=lowest により管理者権限 (UAC 昇格) を一切要求しない。
; ・本アプリ (Core/EnvLoader.cs の Load()) は .env を
;     1) 実行ファイルと同じディレクトリ
;     2) カレントディレクトリ
;     3) %AppData%\VoiceIn
;   の順に探す。もし exe と同じディレクトリ (= {app}) に .env を置いてしまうと、
;   本来読まれるべき %AppData%\VoiceIn\.env (2 台目以降の起動やアップデートで
;   ユーザーが設定した既存の API キー) より先に見つかってしまい、
;   ユーザーの設定を実質的に上書き・無視することになる。
;   → そのため本スクリプトは [Files] で .env を一切配置しない
;     (下記 [Files] セクション参照。該当行が存在しないこと自体が担保)。
; ・%AppData%\VoiceIn\settings.json / history.json / .env / app.log は
;   [Files] / [Dirs] / [UninstallDelete] のいずれでも一切参照しない。
;   インストール時にもアンインストール時にも、このアプリケーションは
;   %AppData%\VoiceIn 配下には一切触れない
;   (作成もしなければ削除もしない。生成はすべてアプリ本体の実行時ロジックに委ねる)。
; ・API キーの入力は、本アプリ自身の初回起動セットアップウィザード
;   (Ui/SetupWindow.xaml.cs。App.xaml.cs が GEMINI_API_KEY / GROQ_API_KEY の
;   いずれも未設定のときに自動的に開く) が担う。そのためインストーラー側で
;   .env のひな形を置く必要が無く、onlyifdoesntexist を使うリスクの高い処理
;   (=既存ファイルとの衝突可能性がある処理) 自体を回避している。
; ・アンインストール時に削除するのは [Files] で {app} 配下にインストールした
;   プログラムファイルのみ。%AppData%\VoiceIn のユーザーデータは削除しない。
; ---------------------------------------------------------------------------

#define MyAppName "Voice In"
#define MyAppVersion "1.0.0"
#define MyAppExeName "VoiceIn.exe"
#define MyAppMutex "VoiceIn_Application_SingleInstance_Mutex"
; dotnet publish の出力先 (build.ps1 と対応させること)
#define PublishDir "..\dist\publish"

[Setup]
; AppId はこのアプリ専用に発番した固定 GUID (再生成しないこと。
; 変えると「別アプリ」として扱われ、アップグレード時に旧バージョンが
; 正しくアンインストールされなくなる)。
AppId={{6E666A68-4AC5-461F-AFE4-8F38933E75A5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}

; --- インストール先: ユーザー単位、管理者権限不要 ---------------------------
DefaultDirName={localappdata}\Programs\VoiceIn
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
; PrivilegesRequiredOverridesAllowed は意図的に設定しない
; (ユーザーが「全ユーザー用にインストール」を選べるようにすると管理者権限を
;  要求する経路が生まれてしまうため、常に lowest 固定とする)。

; 実行中の Voice In を検知できるよう、アプリ本体が使っているミューテックス名を
; そのまま指定する (App.xaml.cs の _mutex = new Mutex(true, "VoiceIn_Application_SingleInstance_Mutex", ...) と同じ名前)。
; インストール/アンインストール前に起動中であれば終了を促すダイアログが出る。
AppMutex={#MyAppMutex}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 10 / WPF が前提とする世代の Windows 10 以降のみをサポート対象とする。
MinVersion=10.0.17763

; --- 出力先・圧縮 -----------------------------------------------------------
; installer\VoiceIn.iss から見て一つ上 = リポジトリルート直下の dist\
; (dist\publish は dotnet publish の出力。dist\*.exe がインストーラー本体)。
OutputDir=..\dist
OutputBaseFilename=VoiceIn-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes

; --- 見た目 ------------------------------------------------------------------
SetupIconFile=..\Assets\VoiceIn.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern

[Languages]
; 同梱を確認済み (Inno Setup 6.7.3 の Languages フォルダに Japanese.isl あり)。
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
japanese.AdditionalTasksGroup=追加のオプション:

[Messages]
; 常駐アプリであることをウェルカムページで明示する。
; [name/ver] は Inno Setup が AppName+AppVersion に置換する組み込みプレースホルダ。
japanese.WelcomeLabel2=このコンピューターに [name/ver] をセットアップします。%n%nVoice In は、設定したキーを長押ししている間だけ音声を録音し、AI で文字起こしした結果をアクティブなウィンドウへ自動的に貼り付ける常駐型ツールです。インストール後はタスクトレイに常駐し、バックグラウンドで動作し続けます。%n%n続行する前に、他のアプリケーションをすべて終了することをお勧めします。
japanese.FinishedLabel=セットアップは、このコンピューターへの [name] のインストールを完了しました。インストールされたアイコンからアプリケーションを起動できます。%n%nVoice In は起動するとタスクトレイに常駐します。初回起動時には、AI プロバイダ (Gemini / Groq) の API キーなどを設定するセットアップ画面が自動的に表示されます。

[Tasks]
; デスクトップショートカットとスタートメニュー登録は必須要件のため、ここでは
; 「常駐アプリを Windows 起動時に自動実行するかどうか」のみを任意タスクとする。
; 既定は必ず unchecked (=オフ) にすること。ユーザーの許可なく常駐アプリを
; 自動起動させないため。
Name: "autostart"; Description: "Windows 起動時に Voice In を自動的に開始する"; GroupDescription: "{cm:AdditionalTasksGroup}"; Flags: unchecked

[Files]
; dotnet publish (自己完結型 win-x64) の出力一式をまるごと {app} へ配置する。
; .env はここで一切参照していない (意図的。上記のデータ保護方針を参照)。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; スタートメニューとデスクトップショートカットは必須要件のため、Tasks による
; 条件付けをせず常に作成する (チェックボックスでオフにできる対象にしない)。
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "音声入力ツール Voice In (タスクトレイ常駐)"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "音声入力ツール Voice In (タスクトレイ常駐)"

[Registry]
; "autostart" タスクが選択された場合のみ、現在のユーザーのスタートアップに登録する。
; HKCU (管理者権限不要) のみを使用し、既定では未選択 (上の [Tasks] を参照)。
; uninsdeletevalue によりアンインストール時にこの値だけを確実に削除する
; (%AppData%\VoiceIn のユーザーデータには一切触れない)。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VoiceIn"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; インストール完了後にアプリを起動するかどうかをユーザーが選べるようにする
; (Finished ページのチェックボックス。既定でチェック済みだが外すことも可能)。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; ---------------------------------------------------------------------------
; 意図的に何も書いていないセクション:
;
; [UninstallDelete] : ここに %AppData%\VoiceIn を消すエントリを足したくなるが、
;   絶対に追加しないこと。settings.json / history.json / .env / app.log は
;   ユーザーデータであり、アンインストール時も保持する。
;   Inno Setup は既定で [Files] に列挙したファイル (と、インストールにより
;   空になった {app} 配下のディレクトリ) のみを削除するため、
;   %AppData% 配下のファイルは何もしなくても自動的に保護される。
; ---------------------------------------------------------------------------
