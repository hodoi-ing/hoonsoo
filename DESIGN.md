# 훈수(hoonsoo) Design System Specification (Open Design 기반)

> Inspired by: **Open Design — Raycast & Linear App**
> Category: High-Performance Developer Desktop Utility
> Target: Windows 11 / .NET 10 WPF (`Theme.cs`, `SettingsWindow.cs`, `PopupWindow.cs`)

---

## 1. Visual Atmosphere & Principles

훈수(hoonsoo)는 화면의 텍스트와 개발 문서를 즉각 OCR 번역하는 고속 생산성 유틸리티입니다. Raycast와 Linear의 핵심 디자인 철학을 결합하여, 단순한 회색 창이 아닌 **"정밀하게 가공된 고성능 도구"**의 시각적 인상을 구현합니다.

- **Dark-Mode Native Precision**: 단순 `#000000`이나 평범한 회색이 아닌, 은은한 블루 언더톤이 감도는 딥 슬레이트 캔버스(`BgApp: #0F1116`)를 기본으로 사용합니다.
- **Hairline Containment**: 무거운 입체 테두리 대신 `1px`의 미세한 반투명 테두리(`rgba(255, 255, 255, 0.08)`)로 카드를 구분하여 시각적 노이즈를 최소화합니다.
- **Physical Keycaps**: 전역 단축키를 단순 문자열 텍스트박스로 방치하지 않고, 깊이감 있는 3D 키캡 칩(`[Ctrl]` `+` `[Alt]` `+` `[D]`) 시각 요소로 렌더링합니다.
- **Zero System Chrome Leak**: 다크 모드 화면에서 Windows 시스템 기본 스크롤바(흰색 레일)가 노출되는 현상을 제거하고, 트랙이 투명하고 썸(Thumb)이 부드러운 다크 스크롤바로 통일합니다.

---

## 2. Color Palette & Semantic Tokens

### 다크 모드 (Dark Palette - Primary)
| 역할 | 토큰명 | 색상 코드 | 설명 |
| :--- | :--- | :--- | :--- |
| **캔버스 배경** | `BgApp` | `#0E1015` | 깊이감 있는 차가운 다크 네이비 캔버스 |
| **카드 배경** | `BgCard` | `#161920` | 기본 표면에서 1단계 부유한 카드 표면 |
| **카드 호버** | `BgCardHover` | `#1E222B` | 마우스 오버 시 미세하게 밝아지는 표면 |
| **카드 액티브** | `BgCardActive` | `#252A36` | 선택/클릭 상태의 표면 |
| **입력창 배경** | `BgInput` | `#12141A` | 오목하게 들어간 인셋(Inset) 입력 필드 |
| **컨테이너 인셋** | `BgInset` | `#14171E` | 섹션/단축키 슬롯 배경 |
| **주 테두리** | `Border` | `#262B36` | 일반 구분선 및 카드 외곽선 |
| **미세 테두리** | `BorderSubtle` | `#1D212A` | 내부 아이템 간 분할선 |
| **헤어라인** | `Hairline` | `rgba(255,255,255,0.07)` | 상단 하이라이트 엣지 |
| **헤어라인 강조** | `HairlineStrong` | `rgba(255,255,255,0.12)` | 포커스 및 호버 시 엣지 |
| **메인 텍스트** | `TextPrimary` | `#F4F6F9` | 눈부심을 줄인 선명한 오프화이트 |
| **보조 텍스트** | `TextSecondary` | `#96A0B2` | 설명 및 캡션 레이블 |
| **비활성 텍스트** | `TextMuted` | `#677182` | 플레이스홀더 및 힌트 |
| **포인트 액센트** | `Accent` | `#38A8FF` | Raycast/Linear 일렉트릭 블루 |
| **액센트 호버** | `AccentHover` | `#58B6FF` | 버튼 오버 시 밝아지는 블루 |
| **액센트 소프트** | `AccentSoft` | `rgba(56,168,255,0.15)` | 선택 뱃지 및 액티브 배경 틴트 |

### 라이트 모드 (Light Palette - Soft Slate)
| 역할 | 토큰명 | 색상 코드 | 설명 |
| :--- | :--- | :--- | :--- |
| **캔버스 배경** | `BgApp` | `#F4F5F8` | 차분하고 눈이 편안한 오프화이트 캔버스 |
| **카드 배경** | `BgCard` | `#FFFFFF` | 순백색 카드 표면 |
| **카드 호버** | `BgCardHover` | `#F9FAFC` | 호버 상태 표면 |
| **입력창 배경** | `BgInput` | `#FFFFFF` | 입력 필드 |
| **컨테이너 인셋** | `BgInset` | `#EBEFF5` | 슬롯 및 태그 인셋 |
| **주 테두리** | `Border` | `#DCE1E9` | 카드 및 입력창 테두리 |
| **메인 텍스트** | `TextPrimary` | `#111827` | 고대비 텍스트 |
| **보조 텍스트** | `TextSecondary` | `#4B5563` | 서브 텍스트 |
| **포인트 액센트** | `Accent` | `#0084E8` | 선명한 브랜드 블루 |

---

## 3. Typography & Micro-Hierarchy

- **기본 폰트**: `Segoe UI Variable`, `Segoe UI`, `Pretendard`, sans-serif
- **모노스페이스(단축키/코드)**: `Cascadia Code`, `Consolas`, monospace
- **타이포 스케일**:
  - **Window Title**: 17px / SemiBold (Tracking: -0.2px)
  - **Section Header**: 14px / SemiBold (Color: `TextPrimary`)
  - **Card Title / Setting Label**: 13px / Medium
  - **Description / Helper**: 11.5px / Regular (Color: `TextSecondary`, LineHeight: 1.4)
  - **Keycap Label**: 11px / SemiBold (Font: Consolas, Color: `TextPrimary`)
  - **Footer Status**: 11.5px / Regular (Color: `TextMuted`)

---

## 4. Component Refinements

### A. 전역 단축키 키캡 (Physical Keycaps)
- 단축키 슬롯 내부를 텍스트 박스로만 두지 않고, 개별 키마다 `Keycap` 배지를 배치:
  - Background: `BgCard` (`#161920`)
  - Border: `1px solid Border` (`#262B36`) + Inset Top Highlight
  - CornerRadius: 4px
  - Padding: `3px 7px`
  - Font: 11px Consolas Medium

### B. 다크 테마 스크롤바 (Integrated ScrollBar)
- OS 기본 라이트 스크롤바 완전 교체:
  - Width: 6px
  - Track: `Brushes.Transparent` (배경과 일체화)
  - Thumb: `CornerRadius 3px`, Background `rgba(255,255,255,0.18)`, Hover 시 `rgba(255,255,255,0.32)`
  - 상/하단 화살표 버튼 제거 (현대적인 미니멀 오버레이 스크롤바)

### C. 카드 및 컨테이너 레이아웃 (Card Structure)
- 좌측 4개의 단축키 카드는 시각적 피로도를 줄이기 위해 일체감 있는 마진(간격 8px)과 호버 피드백 통일.
- 우측 설정 그룹(화면 테마, 동작 옵션)과 상하 대칭 정렬.
- 하단 푸터(저장 버튼 영역)에 은은한 상단 구분선(`Hairline`)을 추가하여 떠다니는 느낌 해소.
