# GF Music Manager

[English](README.md) | [日本語](README.ja.md)

## ダウンロード

[GF Music Manager v0.9.0-beta.1をダウンロード](https://github.com/mirage-gamefront/GF-Music-Manager/releases/tag/v0.9.0-beta.1)

Version: `0.9.0-beta.1`

GF Music Managerは、Mod Organizer 2（MO2）に導入されたSkyrim Special Edition / Anniversary Edition向け音楽MODを解析し、採用する曲と適用先を整理して、管理用MOD `GF Music Product` を生成するWindowsアプリです。

## 主な機能

- MO2の有効MODと、任意で無効MODをスキャン
- ルーズファイルとBSA内のXWM音源を解析
- Music Type、Music Track、Location、Region、Cell、WorldSpaceの関連を表示
- パス競合、内容一致、類似候補を確認して採用・除外を選択
- XWM音源を変換せずに試聴
- Music Track、MTD、Cell用SkyPatcher設定、任意のWorldSpace上書きを生成
- 既存の`GF Music Product`を読み込み、編集状態を復元して再生成
- 生成確定前にESP、MTD、JSON、音源参照を自動診断
- 日本語UIと英語UIを設定画面から切り替え

## 必要環境

- Windows 10またはWindows 11（64-bit）
- Skyrim Special EditionまたはAnniversary Edition
- Mod Organizer 2
- Music Type Distributor（生成したMusic Type、Location、Region設定をゲームへ反映する場合）
- SkyPatcher（生成したCell設定をゲームへ反映する場合）

配布物は次の2種類です。

- `win-x64-self-contained`：.NETランタイム同梱版です。通常はこちらを使用してください
- `win-x64-framework-dependent`：軽量版です。.NET 8 Desktop Runtime x64が別途必要です

Music Type DistributorとSkyPatcherが見つからない場合、GF Music Managerは生成確認画面に警告を表示します。アプリのスキャンと確認は続行できますが、対応する生成設定をゲームへ反映するには前提MODを導入してください。

## インストール

1. 使用するZIPを任意のフォルダへ展開します
2. MO2の`mods`フォルダ内ではなく、通常のアプリ用フォルダへ配置します
3. `GfMusicManager.exe`を起動します（隣の`dll`フォルダは移動・削除しないでください）
4. 上部の設定画面でMO2ルートと使用するプロファイルを選択して保存します

更新時はアプリを終了してから、新しい配布ファイルへ差し替えてください。生成済みの`GF Music Product`はMO2側に保存されるため、アプリ本体とは別に管理されます。

## 基本的な使い方

1. 設定画面でMO2ルート、プロファイル、WorldSpace出力の有無を確認します
2. 必要なら「無効MODを含める」を有効にしてスキャンします
3. 音源一覧、適用先、警告、重複候補を確認します
4. 曲を試聴し、採用・除外とMusic Type割り当てを調整します
5. バニラ音源を残すか選択します
6. 生成確認画面で、生成ESPの有効化と採用曲の元ESPを無効化するか確認します
7. 「生成してMO2へ配置」を実行します
8. 診断OK、生成件数、MO2左ペインと右ペインの配置を確認します

生成先は次の固定フォルダです。

```text
<MO2ルート>\mods\GF Music Product
```

生成MODには、構成に応じて次のファイルが含まれます。

- `GF Music Product.esp`などのESLフラグ付き生成プラグイン
- MTD設定ファイル
- Cell用SkyPatcher設定
- 競合に負ける音源を保持する`Music`フォルダ
- 再編集用の`GFMusicProduct.json`

## 重要な注意事項

- 元MODのファイルは変更しません
- 元ESPの有効・無効は、生成前の確認画面で選択した場合だけ変更します
- 元ESPに音楽以外のレコードが含まれる場合、それらは`GF Music Product`へ引き継がれません。無効化前に内容を確認してください
- WorldSpace用レコードを有効にすると、同じWorldSpaceを変更するMODと競合する可能性があります
- 生成後診断は生成物の整合性を確認しますが、ゲーム内での再生を保証するものではありません
- `GF Music Product`はMO2左ペインの最下段へ配置されます。右ペインの順序はLOOTなどのロードオーダー管理に従います

## トラブル対処

### スキャンまたは生成に失敗する

MO2ルートとプロファイルが正しいこと、対象ドライブへ読み書きできること、GF Music ManagerやMO2のファイルを別プロセスがロックしていないことを確認してください。

### Music TypeまたはCell設定が反映されない

Music Type DistributorとSkyPatcherが導入・有効化されていることを確認してください。生成MOD内にMTD設定とCell用SkyPatcher設定が存在することも確認してください。

### 元の音楽MODがまだ再生される

生成確認画面で元ESPを有効のまま残した場合、元ESP側のMusic Type、Track、Cell、Location、Regionの設定が競合する可能性があります。MO2の右ペインと競合表示を確認してください。

### 生成MODが優先度0にある

正常な生成では`GF Music Product`がMO2左ペイン最下段の最高優先度へ配置されます。再生成後も優先度0になる場合は、最新ログとMO2プロファイルの`modlist.txt`を添えて報告してください。

## ログ

ログファイル出力は初期設定でOFFです。問題を調査するときは、設定画面の「ログファイルを出力する」をONにしてから操作を再現してください。

ONの場合、ログは次のフォルダへ保存されます。

```text
%LOCALAPPDATA%\GF Music Manager\logs
```

問題報告には、発生時刻に対応する最新ログ、使用したMO2プロファイル名、実行した操作、画面に表示されたエラーを含めてください。

## アンインストール

アプリ本体のフォルダを削除してください。生成MODも不要な場合は、MO2で`GF Music Product`を無効化してから、MO2の操作で削除してください。

## ライセンスとソースコード

GF Music Managerは`GPL-3.0-only`で配布します。ライセンス本文は同梱の`LICENSE.txt`を参照してください。

このバイナリに対応するソースコードは、バイナリと同時に配布する`GF-Music-Manager-v0.9.0-beta.1-source.zip`から取得できます。

使用ライブラリと各ライセンスは、バイナリZIPの`Documentation\THIRD-PARTY-NOTICES.txt`と
`Documentation\LICENSES`を参照してください。source ZIPでは
`THIRD-PARTY-NOTICES-GF-MUSIC-MANAGER.txt`と`licenses\GfMusicManager`に収録しています。

### ソースからのビルド

.NET 8 SDKを導入し、source ZIPのルートで次を実行します。

```powershell
dotnet build .\src\GfMusicManager\Desktop\GfMusicManager.Desktop.csproj --configuration Release
dotnet test .\tests\GfMusicManager.Core.Tests\GfMusicManager.Core.Tests.csproj --configuration Release
dotnet test .\tests\GfMusicManager.Desktop.Tests\GfMusicManager.Desktop.Tests.csproj --configuration Release
dotnet test .\tests\SkyrimScan.Core.Tests\SkyrimScan.Core.Tests.csproj --configuration Release
```
