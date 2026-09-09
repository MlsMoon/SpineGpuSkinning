# SpineGpuSkinning

[English](../README.md) | [简体中文](README.zh-Hans.md) | 日本語

[spine-unity](http://esotericsoftware.com/spine-unity) の SkeletonAnimation 向け GPU スキニングです。フレームごとの CPU スキニング / メッシュ再構築 / 頂点アップロードを GPU 頂点シェーダへ移し、Spine のソースは一切変更しません。

> CPU はボーン評価（AnimationState、ミックス、イベント、物理、BoneFollower、ランタイムスキン）を担当します。GPU が頂点スキニング、メッシュ組み立て、アップロードを行います。同じアトラスページを共有するインスタンスは 1 回の `DrawMeshInstancedIndirect` で描画されます。

## 要件

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- プロジェクトに spine-unity **4.2**（spine-csharp 4.2）が導入済みであること。本リポジトリに Spine ランタイムは**含まれません**。各自で導入し [Spine Runtimes License](http://esotericsoftware.com/spine-runtimes-license) に従ってください。

## クイックスタート

1. `SpineGpuSkinning` フォルダをプロジェクトの `Assets/` 配下へコピーします（例: `Assets/Plugins/`）。
2. `SkeletonAnimation` 付きの GameObject を選択します。
3. Add Component → `Gpu Skeleton Renderer`。

インポート時に各 `SkeletonDataAsset` が自動監査・ベイクされます。コンテナ（監査レポートとスキン組み合わせごとのプロトタイプメッシュ）は当該アセットのサブアセットです。コンポーネントは次を行います。

- 現在のスキン組み合わせのベイク済みエントリを検索（ランタイムはベイクしません。未ヒットなら警告して CPU 経路のまま）
- エントリがあれば GPU へ切替（`updateMode = EverythingExceptMesh`、`MeshRenderer` を無効化）
- 毎フレーム `UpdateComplete` 後に 3x2 ボーンパレットを出力
- アトラスページ単位でバッチ提出

コンポーネントを外すか無効にすると元の CPU 経路に戻り、残留はありません。

監査失敗時は**黙って CPU 経路のまま**です。操作は不要です。

## サンプル

`Example/` には冒険家 **SampleDude** とロボット **SampleRobot** が含まれ、各 3 種のスキンがあります。
`Example/GpuSpineComparison.unity` を開いて Play Mode に入ります。

- **FPS**、平均フレーム時間、直近の CPU/GPU 測定値を表示。
- 位置とアニメーションを維持して CPU/GPU スキニングを切り替え。
- 初期人数は 32、UI で 0–1000 に変更可能。スキンの混在と配色指定に対応。
- SVG 原稿、Spine 4.2 データ、Prefab、GPU ベイクデータを同梱。
- Unity、URP、spine-unity のみが必要で、ゲーム固有のフレームワークに依存しません。

**Tools / GPUSpineSkin / Build Comparison Example** で再生成できます。詳細は `Example/README.md`。

## 適した Case と性能の限界

**GPU スキニングが常に高速とは限りません。少数・低頂点数なら CPU の方が軽い場合があります。**
両モードともアニメーションとボーン・制約の評価は CPU で行います。GPU モードには行列、Deform、
スロット色の転送、可視性、ソート、バッファと描画管理の追加コストがあります。

| Case | 判断の目安 |
| --- | --- |
| 少数の Region 主体の軽いキャラクター | CPU を基準に比較。GPU の追加コストが上回る可能性があります。 |
| メッシュ・スキン・アトラスを共有する多数の可視インスタンス | GPU の候補。実際のバッチ数と CPU メッシュ処理を測定してください。 |
| 高密度のウェイト付きメッシュと変形する衣装 | 頂点処理の削減余地がありますが、Deform 評価と転送は残ります。 |
| 頻繁なスキン・描画順変更、多数の材質、透明物の交差 | バッチが分割され、必ずしも少数の DrawCall にはなりません。 |
| クリッピング、影、独自の複数 Pass | 追加コストと統合が必要です。まず CPU/GPU の表示一致を確認してください。 |
| アニメーション評価、AI、フィルレートなどがボトルネック | スキニングだけを移してもフレーム時間は改善しない場合があります。 |

**Simple duo** は各 20 ボーン、16 スロット、159 可視頂点、3 スキンの基本サンプルです。
**Complex courier**（初期設定）は 48 ボーン、24 スロット、約 1170 ベイク頂点、3 スキンに加え、
4 個の衣装メッシュの Deform、24 点の非凸クリッピング、アタッチメント・色・描画順の変化を含みます。
制作現場で使う技術の種類を示しますが、特定ゲームの照明、影、輪郭線、相互作用すべての代替ではありません。

同じ Case、人数、スキン分布、解像度、カメラでウォームアップ後に繰り返し測定してください。
FPS だけで CPU 使用率の低下を判断せず、Profiler で CPU/GPU 時間、GC、描画数も確認します。
Editor の比較は操作確認に便利ですが、最終的な性能評価では Player を優先してください。

## エディタメニュー

共通ツールは **`Tools/GPUSpineSkin`** にあります。選択中の `SkeletonDataAsset` に作用します。

| メニュー | 内容 |
|---|---|
| `Tools/GPUSpineSkin/Rebake Selected` | フィンガープリントまたはエントリキーが変わったとき再ベイク |
| `Tools/GPUSpineSkin/Force Rebake Selected` | フィンガープリントを消して強制再ベイク |
| `Tools/GPUSpineSkin/Log Audit Report` | ベイクせず監査ログのみ |
| `Tools/GPUSpineSkin/Dump Baked Data` | コンテナ、エントリ、宣言コンボを出力 |
| `Tools/GPUSpineSkin/Inspect Default Shader` | `GpuSpine/URP/Skeleton` のコンパイル状態 |
| `Tools/GPUSpineSkin/Build Comparison Example` | 両キャラクターと群衆比較シーンを再生成 |

`SkeletonDataAsset` 選択時は Project 右クリックの `Assets/GpuSpine/` にも Rebake / Audit があります。ホストプロジェクトの一時スモークは `Tools/GPUSpineSkin/Temp/...` を使ってください。

## CPU フォールバック規則

| 検出内容 | 動作 |
|---|---|
| Deform タイムライン | 対応。CPU の `slot.Deform` を毎フレーム deform バッファへ載せ、ボーン加重の前に適用 |
| スロットカラー (RGBA / RGB / Alpha) | 対応。各スロット `R/G/B/A` を毎フレームアップロードして頂点色に乗算 |
| Dark color (RGBA2 / RGB2) | CPU フォールバック（tint black 未実装） |
| Texture sequence | CPU フォールバック |
| 現在のスキン組み合わせにベイクなし | 警告して CPU フォールバック |
| `zSpacing` がベイク値 0 と不一致 | 警告して CPU フォールバック |
| Attachment タイムライン | 対応（動的スロット）。バリアントを事前ベイクし、未選択は頂点シェーダで折りたたみ |
| Draw order タイムライン | 既定は CPU。`AllowDrawOrderTimeline` で GPU 許可 |
| Clipping | 既定は CPU。`IgnoreClipping` で GPU 許可（非クリップ描画） |
| 頂点が 4 本超のボーン影響 | 切り詰めて再正規化し、警告のうえ GPU 継続 |

## カスタム Shader / RenderPass

頂点関数の先頭で `Runtime/Shaders/SpineGpuSkinning.hlsl` を include しスキニング関数を呼びます。完全な例は `Skills~/gpuspine-use-plugin/references/integration-examples.md`。

バッチ API で生存中のバッチを列挙し、独自 RT へ再提出できます。同上。

## Agent skills

`Skills~/` に次を同梱しています。

- `gpuspine-use-plugin` — 導入、フォールバック、トラブルシュート
- `gpuspine-develop-plugin` — アーキテクチャ、ベイク意味論、バッファレイアウト

プロジェクトの agent skill ディレクトリ（例: `.agents/skills/`）へコピーしてください。

## ライセンス

MIT（`LICENSE` を参照）。Spine ランタイムは Spine Runtimes License のままで、本リポジトリには含まれません。
