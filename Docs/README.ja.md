# SpineGpuSkinning

[English](../README.md) | [简体中文](README.zh-Hans.md) | 日本語

[spine-unity](http://esotericsoftware.com/spine-unity) の `SkeletonAnimation` 向け GPU スキニングです。フレームごとの CPU メッシュ再構築 / 頂点アップロードを GPU 頂点シェーダへ移し、Spine のソースは一切変更しません。

> CPU はボーン評価（AnimationState、ミックス、イベント、物理、BoneFollower、ランタイムスキン）を担当します。GPU が頂点スキニング、メッシュ組み立て、アップロードを行います。互換インスタンスはアップロード資源を共有し、描画範囲はキャラ単位の透明順を保ちます。

**エージェント:** まずリポジトリ直下の [`AGENTS.md`](../AGENTS.md) を読んでください。スキルは [`Skills~/`](../Skills~/README.md)（Unity は `~` フォルダを無視します）。機械可読インデックスは [`llms.txt`](../llms.txt) です。古い README から API を推測しないでください。現行契約は `AGENTS.md` が優先します。

## 要件

- Unity 2022.3+
- Universal Render Pipeline (URP) 14
- プロジェクトに spine-unity **4.2**（spine-csharp 4.2）が導入済みであること。本リポジトリに Spine ランタイムは**含まれません**。

## クイックスタート

1. `SpineGpuSkinning` フォルダをプロジェクトの `Assets/` 配下へコピーします。
2. `SkeletonAnimation` 付きの GameObject を選択します。
3. Add Component → `Gpu Skeleton Renderer`。

インポートではベイクしません。コンテナが無いときは Inspector の `Bake Skeleton Data`、または `Tools/GPUSpineSkin/Bake All Skeleton Data` / `Rebake Selected` を使います。ランタイムはベイクしません。コンポーネントを外すか無効にすると元の CPU 経路に戻ります。

## サンプル

`Example/GpuSpineComparison.unity` に **SampleDude**、**SampleRobot**、**ComplexCourier**、**UltraCourier** があります。**Tools / GPUSpineSkin / Build Comparison Example** で再生成できます。詳細は `Example/README.md`。

## エディタメニュー

共通ツールは **`Tools/GPUSpineSkin`** です。`Bake All Skeleton Data` は選択に依存せず、プロジェクト内の全 `SkeletonDataAsset` を Rebake します（最新ならスキップ）。指紋 **v2** は依存ファイルの**内容**ハッシュです。タイムスタンプだけが変わる VCS checkout では再ベイクしません。手でメッシュを消したときや、ソース未変更で焼き直すときは Force Rebake を使います。

## CPU フォールバック規則

| 検出内容 | 動作 |
|---|---|
| Deform タイムライン | 対応。CPU の `slot.Deform` をアップロードし、ボーン加重の前に適用 |
| スロットカラー (RGBA / RGB / Alpha) | 対応。各スロット `R/G/B/A` をアップロードして頂点色に乗算 |
| Dark color (RGBA2 / RGB2) | CPU フォールバック（tint black 未実装） |
| Texture sequence | CPU フォールバック |
| ベイクなし / `FormatVersion` 不一致（現行は 5） | CPU フォールバック |
| `zSpacing` がベイク値 0 と不一致 | CPU フォールバック |
| Attachment タイムライン | 対応（動的スロット）。未選択バリアントは頂点シェーダで折りたたみ |
| Draw order タイムライン | 対応。ベイク済み `DrawOrderLayouts` が実行時順序を再現。レイアウトのない古いコンテナは CPU。`AllowDrawOrderTimeline` は未使用の互換フィールド |
| Clipping | 対応。GPU フラグメントクリップ。`IgnoreClipping` はそのインスタンスだけクリップをスキップ（range をゼロ送信）。レイアウトのない古いコンテナは CPU |
| 頂点が 4 本超のボーン影響 | 切り詰めて再正規化し、GPU を継続 |

完全な規則は `Skills~/gpuspine-use-plugin/references/fallback-rules.md`。

## カスタム Shader / RenderPass

頂点関数の先頭で `Runtime/Shaders/SpineGpuSkinning.hlsl` を include し、8 引数の `GpuSpineSkinToWorld` を呼びます。クリップが必要ならフラグメントで `GpuSpineClip` を呼びます。

カスタム GPU シェーダはアトラスページ材質に載せ、**同じシェーダ**のマテリアルを `MaterialOverride` に割り当てます。別ファミリーのシェーダではページシェーダは置き換わりません。

`GpuSkinningManager.GetBatches(camera)` または `GetBatches(camera, source)` でドローを列挙します。`SubmeshIndex` は 0 で、実範囲は args の IndexStart/IndexCount です。

## ランタイム切替

- `IncludeInRuntimeSwitch`（既定オフ）は `GpuSpineRuntimeSwitch` に登録し、ホストから CPU/GPU を切り替えます。
- `CopyPropertyBlockToCustomData` は `WriteInstanceData` の前に MeshRenderer MPB を Custom0/Custom1 へコピーします。

## 診断

`GpuSpineDiagnostics.EnableLogging` と `EnableAutomaticValidation` はどちらも `const bool` で既定 `false` です。通常の GPU スキニングは検証ゲートの後ろに置きません。

## Agent skills

`Skills~/` に `gpuspine-use-plugin` と `gpuspine-develop-plugin` があります。ホストの `.agents/skills/` または `.cursor/skills/` へコピーしてください。このリポジトリでは先に `AGENTS.md` を読みます。

## ライセンス

MIT（`LICENSE` を参照）。Spine ランタイムは Spine Runtimes License のままで、本リポジトリには含まれません。
