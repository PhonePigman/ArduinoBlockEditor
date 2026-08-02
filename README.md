# ArduinoBlockEditor

WPFで作成された、Arduino向けの直感的なブロックプログラミングエディタです。

## 概要
視覚的にブロックを組み合わせることで、直感的にArduinoプログラムを作成できます。  
拡張モジュール（MOD）機能に対応しており、簡単に機能を拡張できます。

## 主な機能
- **直感的なブロック編集**: ドラッグ＆ドロップで簡単にコードを構成できます。
- **MOD機能対応**: `mods` フォルダにMODを追加することで、利用できるブロックを自由に拡張可能。
- **ModBuilder同梱**: 自作のMODを簡単に構築できるツールを同梱しています。

## 動作環境
- **OS**: Windows 10 / 11 (64-bit)
- **必須環境**: .NET 8.0 Desktop Runtime / **Arduino IDE**

## ダウンロードと使い方

### 1. 準備と起動
1. 右側の **[Releases](https://github.com/PhonePigman/ArduinoBlockEditor/releases)** から最新の ZIP ファイルをダウンロード
2. ZIP をお好きなフォルダに解凍
3. `ArduinoBlockEditor.exe` を実行

### 2. プログラムの書き込み手順
1. 本アプリ上でブロックを組み替え、Arduino用コードを生成します。
2. 生成されたコードをコピーします。
3. **Arduino IDE** を起動し、コードを貼り付けます。
4. Arduino IDE 上からマイコンボードへ書き込みを行ってください。

> ⚠️ **ご注意**  
> 本アプリはコードを生成するためのツールです。本アプリから直接マイコンボードへ書き込む機能はありませんので、書き込みには **Arduino IDE** をご使用ください。

---

## 利用時のお願い
- 再配布・改変して公開する場合は、本リポジトリ（[PhonePigman/ArduinoBlockEditor](https://github.com/PhonePigman/ArduinoBlockEditor)）へのリンクを明記してください。
