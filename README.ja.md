# NTFS Tracer

NTFS Tracer は、NTFS の変更ジャーナルとトランザクションログを調べる Windows デスクトップアプリです。ファイル名、パス、ID で記録を探し、ファイルごとの操作履歴や名前変更の前後を確認できます。

[한국어](README.ko.md) · [English](README.md)

## 使い方

Windows x64 の配布 ZIP をすべて展開し、`NtfsTracer.exe` を実行します。軽量版には .NET 10 Windows Desktop Runtime (x64) が必要です。

1. 資料を指定せずに起動すると、**設定**が開きます。抽出済みの `$J`、`$MFT`、`$LogFile`、またはディスクイメージを選択するか、ファイルやフォルダーをウィンドウにドロップします。ここでテーマを切り替えたり、インデックスや CSV の保存先のパスを貼り付けたりできます。
2. 解析を開始します。ディスクイメージを開く場合は、抽出するボリュームと項目を選びます。
3. ログの一覧から記録を選びます。概要で操作内容とパスの根拠を確認できます。必要に応じて Hex で元のバイト列を確認できます。`F1` キーでアプリ内ガイドを開けます。

| 調べたい項目 | 使用する資料 |
| --- | --- |
| 変更履歴、以前の名前、ファイルごとの操作、調査の手がかり | `$J` |
| パスの補完 | `$J` または `$LogFile` と、同じボリュームの `$MFT` |
| トランザクション、LSN、Redo・Undo のデータ | `$LogFile` |

パスやファイル ID、親フォルダー ID で絞り込み、同じファイルの変更前後を比較できます。検索結果の CSV 出力、NTFS Log Tracker の結果表示、指定したファイルからの痕跡検索にも対応しています。MFT から補完したパスは収集時の情報を使うため、操作当時のパスと異なる場合があります。

物理ディスクイメージから解析に必要なファイルを抽出できるのは NTFS ボリュームです。FAT32、exFAT、ReFS などは解析対象外です。論理証拠イメージ（L01・Lx01）では、収集済みのファイルから NTFS の解析に必要なものを選べます。

元の証拠ファイルは読み取り専用で開きます。抽出したファイルと SQLite インデックスは別の場所に保存します。新規インストール時の既定の保存先は `%LOCALAPPDATA%\NtfsTracer` です。JournalDesk から更新した場合は、既存の設定とインデックスを引き続き使用できます。対応形式と制約は [イメージモジュールの説明](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/README.md) に記載しています。

## ビルド

[ビルド手順と依存関係](README.md#build-from-source) · [EWF ライブラリのビルド](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/Logical/Native/README.md) · [インデックス形式](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/engine/INDEX_FORMAT.md)


## ライセンス

プロジェクト独自のコードには [PolyForm Noncommercial 1.0.0](LICENSE) が適用されます。営利企業での社内調査、有償の受託調査、商用製品への組み込みには、事前の書面による許可が必要です。利用目的と連絡方法を [GitHub Issues](https://github.com/absolroot/NTFS-Tracer/issues/new) でお知らせください。公開の Issue に事件情報や証拠を添付しないでください。[外部コンポーネント](THIRD-PARTY-NOTICES.md) には、それぞれのライセンスが適用されます。
