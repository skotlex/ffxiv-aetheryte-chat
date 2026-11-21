# Dalamud 스타일 — Aetheryte Dark

kagerou 미터기 Umbra 스킨([../kagerou-umbra](../kagerou-umbra/README.md))과 같은 색으로 맞춘 Dalamud 스타일입니다.
짙은 회색 바탕에 얇은 테두리를 두르고, 버튼·입력칸도 테두리 있는 회색으로 그립니다. 붉은 강조색은 쓰지 않습니다.

## 쓰는 법

- **Aetheryte Chat 창에만:** 설정 > 스타일 탭의 창 스타일에서 `Aetheryte Dark` 를 고릅니다 (플러그인에 들어 있음)
- **다른 Dalamud 창에도:** [aetheryte-dark.dalamud-style.txt](aetheryte-dark.dalamud-style.txt) 내용을 복사한 뒤
  Dalamud 설정 > 스타일 편집기에서 클립보드에서 가져오기를 누릅니다.
  플러그인 스타일 탭에서 `Aetheryte Dark` 를 고르고 `Dalamud 용으로 복사` 를 눌러도 같은 문자열이 복사됩니다

## 고칠 때

이 파일은 `src/AetheryteChat/Windows/BuiltInStyles.cs` 의 스타일을 Dalamud 의 `Serialize()` 로 만든 것입니다.
스타일 색을 고치면 플러그인 버튼으로 다시 복사해 이 파일을 갈아 끼웁니다.
