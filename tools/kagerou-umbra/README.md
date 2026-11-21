# kagerou Umbra 스타일

[kagerou](https://github.com/hibiyasleep/kagerou) 미터기 오버레이를 Umbra 설정 창처럼
어두운 카드형으로 바꾸는 사용자 CSS 입니다.

## 적용

kagerou 설정의 사용자 CSS 칸 내용을 모두 지우고 [kagerou-umbra.css](kagerou-umbra.css) 전체를 붙여 넣은 뒤 저장합니다.

설정 창에서 탭의 열 구성을 바꾸고 저장하면 kagerou 가 값 줄만 새로 그리고 헤더(열 이름 줄)는 예전 구성대로 둡니다
(kagerou 자체 동작). 열을 바꾼 뒤에는 오버레이의 ⋯ 메뉴에서 새로고침하거나, 다른 탭을 눌렀다 돌아오면 헤더가 맞춰집니다.

색·투명도·게이지 진하기는 파일 맨 위 `:root` 의 `--um-*` 값만 고치면 전체에 반영됩니다.
규칙을 직접 더할 때는 선택자 앞에 `html body` 를 붙입니다. kagerou 는 기본 CSS 를 사용자 CSS 보다
늦게 붙여서, 우선순위가 같으면 기본값이 이깁니다.

## 미리보기

```
python build_preview.py
```

`preview/index.html` 을 브라우저로 열면 8인·24인·대기 화면이 함께 보이고, 원본과 번갈아 볼 수 있습니다.
CSS 를 고친 뒤 새로고침 버튼을 누르면 바로 반영됩니다.

- DPS 탭 열 구성을 바꿔 보려면 `preview/overlay.html?p8,cols=i.icon,i.name,deal.maxhit,deal.pct` 처럼
  열 키를 쉼표로 이어 엽니다 (열 키는 `kagerou/share/lang/ko.json` 의 `col` 참고)
- kagerou 원본이 `kagerou/` 에 없으면 스크립트가 GitHub 에서 받아 옵니다
- `kagerou/` (GPL-3.0, 직업 아이콘 포함) 와 만들어진 `preview/` 는 저장소에 올리지 않습니다
- 전투 데이터는 지어낸 값입니다
