# Hoonsoo (훈수)

Windows 10/11 x64, C# / .NET 10 LTS / WPF. 마우스 아래 영어를 한국어로 번역하는 트레이 앱입니다.

## 저장소에 없는 것

빌드 산출물과 작업 스크래치는 커밋하지 않습니다. 아래 경로는 로컬에만 있습니다.

| 경로 | 내용 |
|---|---|
| `outputs/` | 배포본 `hoonsoo-win-x64/`(및 `.zip`), `prerequisites/vc_redist.x64.exe`. `scripts/build.ps1`이 테스트 통과 후 생성·갱신합니다. |
| `work/` | 준비된 .NET 10 SDK(`work/dotnet/`), 렌더·AR 프로브 프로젝트(`work/probe-render`, `work/probe-ar`), 빌드/테스트 로그. |
| `.insane-research/`, `.kkirikkiri/`, `.pumasi/`, `.ui-polish-backup/` | 작업 중 만들어진 에이전트 세션 산출물과 백업. |

그래서 새로 클론한 저장소에는 `outputs/`도 `work/dotnet/`도 없습니다. 먼저 시스템에 .NET 10 SDK를 설치한 뒤 `./scripts/build.ps1`을 실행하세요.

## 실행

배포본은 `outputs/hoonsoo-win-x64/hoonsoo.exe`입니다(바탕화면 `훈수` 바로가기가 가리키는 경로). 같은 폴더를 압축한 것이 `outputs/hoonsoo-win-x64.zip`이고, `scripts/build.ps1`이 테스트 통과 후 이 두 가지를 함께 갱신합니다. `outputs/hoonsoo-performance-win-x64/`는 이전 세션에서 만든 별도 빌드로, 그 빌드 스크립트가 저장소에 없어 **최신 수정이 들어 있지 않습니다**(참고용 보관). **배포 폴더 전체**를 함께 보관하세요. .NET 런타임은 포함됩니다. Tesseract CPU OCR에는 Microsoft Visual C++ 2015–2022 x64 Runtime이 필요하며, 없는 PC에서는 배포본의 `prerequisites/vc_redist.x64.exe`를 설치합니다.

1. 기본 번역은 API 키 없이 사용합니다(인터넷 필요). 설정에 API 키·모델·Base URL 항목은 없습니다 — 번역이 그것들을 쓰지 않기 때문입니다.
2. 다른 앱의 영어 위에 마우스를 두고 기본 단축키(`Ctrl + Alt + D`)를 누르면 마우스 아래 팝업 번역이 뜹니다. 팝업은 캡처한 문장을 덮지 않는 자리(오른쪽 → 왼쪽 → 아래 → 위 순)에 열리고, **카드 아무 곳이나 끌어 옮길 수 있습니다**. 한 번 옮기면 자동 재배치가 꺼져 사용자가 둔 자리를 유지합니다.
3. **전체 화면 AR 자막 HUD (`Ctrl + Shift + Space`)**: 활성 모니터의 모든 영문을 Windows Media OCR로 초고속(수십 ms) 검출하여 영문 바로 아래에 1:1 한글 자막 칩을 오버레이로 띄웁니다. 자막 판은 불투명 배경과 `ClearTypeHint`·`TextFormattingMode=Display`로 그려지고 창은 물리 픽셀에 정렬된 뒤에만 그려지므로, 125%/150% 배율에서도 글자가 뭉개지지 않습니다. 마우스 클릭 관통(`WS_EX_TRANSPARENT`)을 지원하며, 아무 곳이나 클릭하거나 `ESC`를 누르면 즉시 닫힙니다.
4. UI Automation → 140ms 후 한 번 재탐색 → 주변 로컬 OCR 순서입니다. 실패하면 팝업의 **영역 OCR**에서 직접 드래그합니다. ESC는 팝업 또는 영역 선택을 닫습니다.
5. **↻ 마지막 드래그**는 저장된 영역을 다시 OCR하고 번역합니다. **영역 OCR (새로 드래그)**는 새 영역을 지정합니다. 결과를 어디에 보여줄지는 설정의 **영역 번역 표시** 카드에서 **표시 위치**를 `화면 위 오버레이` / `번역 팝업 창` / `둘 다 (기본)` 중에서 고릅니다. `화면 위 오버레이`는 결과를 선택 영역 바로 아래 큰 자막 판으로 띄우고, `둘 다`는 같은 내용을 팝업에도 함께 표시합니다. 기본값에서 화면 판의 글자는 같은 배율의 AR 자막보다 항상 큽니다(AR 상한 17 DIP < 영역 하한 18 DIP). 읽을 수 없는 드래그(8픽셀 미만 또는 1,200만 픽셀 초과)는 거부되고 그 이유를 활성 표면(팝업, 팝업을 쓰지 않으면 트레이 알림)으로 알려 줍니다. ESC로 취소한 경우에는 아무것도 띄우지 않습니다. 기본 번역 팝업이 떠 있는 상태에서 영역 OCR을 눌러도 영역 선택이 정상적으로 열립니다(예전에는 이 경우 선택 창이 뜨지 않고 조용히 끝났습니다).
6. 개발용어는 로컬 사전에서 뽑은 용어와 한국어명 목록이며 네트워크를 쓰지 않습니다. 원문 보기와 복사도 제공됩니다.
7. **화면 테마**: 설정 창 `일반` 탭의 **화면 테마**에서 `시스템 설정 따르기 / 라이트 / 다크`를 고릅니다. 고르는 즉시 미리보기되고, 하단 **저장**을 눌러야 유지됩니다. 팝업의 펼침 머리글(원문 보기·개발용어)도 팔레트를 따르므로 다크 모드에서 밝은 글자로 보입니다. `시스템 설정 따르기`는 Windows 앱 테마(`설정 > 개인 설정 > 색`)를 따르며, 앱이 떠 있는 동안 Windows 테마를 바꾸면 자동으로 따라갑니다.
8. **영역 번역 표시**: 설정 창 `일반` 탭 우측 맨 위 카드에서 **표시 위치**와 화면 표시 크기를 정합니다. **표시 위치**는 드래그 영역 결과에만 적용되며, 커서 아래 번역(`기본 번역`, 기본 `Ctrl + Alt + D`)은 설정과 무관하게 항상 팝업으로 표시됩니다. **전체 화면 자막**(AR HUD)과 **영역 번역**(드래그 결과 판)의 크기는 60%~200%에서 각각 따로 고르며(기본 100%), 각 배율이 그 폰트의 하한·상한에 함께 곱해지므로 같은 배율에서는 "영역 판 > AR 자막" 관계가 유지됩니다. 배율은 다음에 화면에 띄울 때부터 적용됩니다. `settings.json`에서 직접 숫자를 고쳐도 되지만 저장할 때 가장 가까운 단계(60/70/80/90/100/110/125/150/175/200)로 맞춰집니다.
앱별 제외 목록은 사용하지 않습니다. 모든 캡처는 사용자가 직접 누른 단축키와 드래그 영역을 기준으로 수행합니다.

## 보안과 비용

설정은 `%LOCALAPPDATA%\hoonsoo\settings.json`. API 키를 받지 않으므로 자격 증명 파일(`credential.bin`)도 만들지 않습니다. 원문·스크린샷을 로그에 기록하지 않습니다. 화면 이미지는 메모리에서만 로컬 OCR에 사용하고 네트워크로 전송하지 않습니다. 번역할 일반 텍스트는 Google 온라인 번역에 전송됩니다. 키 없는 비공식 웹 엔드포인트를 사용하므로 서비스 변경/차단 시 번역이 실패할 수 있습니다. 코드 보호 구간은 전송하지 않습니다. 번역 외의 목적으로 원문을 전송하는 경로는 없습니다.

새 단축키/다시 읽기는 이전 작업을 취소합니다. 일반 무료 번역은 코드 보호 구간 사이의 독립 문장을 최대 4개 동시 요청하고 동일한 전송 문장은 한 번만 요청합니다. 각 위치의 공백과 코드, 결과 순서는 유지합니다. 취소 시 대체 서비스 요청을 시작하지 않습니다. MyMemory 서비스 오류는 번역 결과로 표시하지 않습니다. 일반 번역 결과는 메모리에 최대 100개를 저장하며 오래된 항목부터 하나씩 제거합니다. 번역 요청의 25초 제한과 취소 정책은 유지합니다. 이미 서버에 도착한 요청의 과금까지 취소할 수는 없습니다.

코드·명령·경로·URL·식별자를 고유 placeholder로 보호하고 각 토큰이 정확히 한 번 복원되는지 검사합니다. 임의의 새 제품명/패키지명처럼 문법으로 식별하기 어려운 이름은 모델의 보존 지침도 적용하지만 모든 자연어 입력의 완벽한 인식은 보장되지 않습니다. 토큰이 훼손되면 잘못된 번역을 표시하지 않고 원문/오류를 제공합니다.

### 번역 처리 성능 개선

- 화면 자막: 동일 문구 중복 요청 제거, HTTP 동시 요청 최대 4개, URL 크기를 고려한 배치 및 긴 문장 분할, 성공한 한국어 결과만 최대 2,000개 캐시합니다.
- 구분자가 포함된 원문은 개별 번역합니다. 배치 응답의 개수가 맞지 않으면 개별 요청으로 전환합니다. 배치의 대응은 위치 기반이며, 개수 검사만으로 서비스의 문장 재배열을 검출할 수는 없습니다.
- 고정 150ms HTTP 응답을 사용한 5개 코드 분리 문장 구간 측정: 변경 전 925ms, 변경 후 424ms. 인터넷 지연이나 번역 품질 점수가 아니라 같은 조건의 처리 비교입니다.
- 동일 문장 3회: HTTP 3회 → 1회. 취소된 단일 요청: 후속 대체 요청 포함 2회 → 1회.
- 실제 무료 번역과 화면 자막 번역, 2,249자 화면 문장, 코드 보존, 취소, 캐시, 서비스 오류 처리 스모크를 통과했습니다. 전체 데스크톱 UI 자동화 테스트는 실행하지 않았습니다.
- 참고: [Pot](https://github.com/pot-app/pot-desktop)의 병렬 번역·시스템 OCR, [STranslate](https://github.com/STranslate/STranslate)의 WPF 번역·OCR 구성. 외부 구현 코드를 복사하지 않았습니다.

## 빌드 및 검증

Windows x64, .NET 10 SDK, Visual C++ x64 Runtime, 대화형 데스크톱이 필요합니다. NuGet 패키지는 로컬 OCR용 `Tesseract 5.2.0` 하나입니다. 테스트는 추가 패키지 없는 실행형 단위/통합 테스트 러너입니다.

```powershell
./scripts/build.ps1
# 로컬 작업 폴더에 준비된 SDK를 쓸 때 (저장소에는 포함되지 않습니다):
./scripts/build.ps1 -Dotnet ./work/dotnet/dotnet.exe
```

테스트 러너는 테스트용 창을 잠시 열고 마우스/ESC를 사용한 UI 통합 검증을 수행하므로 실행 중 다른 작업을 피하세요. 테스트용 데이터는 테스트 출력 폴더 안에만 저장합니다. 네트워크나 API Key를 요구하지 않으며 번역 경계는 가짜 HTTP 응답으로 검증합니다. 사용자 프로세스나 데이터는 종료/변경하지 않습니다.

### 창을 띄우지 않는 검증

화면에 아무것도 띄우지 않고 확인할 수 있는 경로입니다(다른 작업 중에도 안전).

```powershell
# 컴파일만 (창 없음)
./work/dotnet/dotnet.exe build src/hoonsoo/hoonsoo.csproj -c Release

# 오프스크린 렌더·측정: 설정 창과 자막 칩/영역 판을 (-4000, -4000)에 두고 RenderTargetBitmap으로 캡처
./work/dotnet/dotnet.exe run --project work/probe-render/render.csproj -- all
# → work/out/*.png (settings-light/dark, ar-chip-*, region-plate-*) + 콘솔 PROBE/PASS/FAIL 수치

# 창·입력 주입이 없는 검사만 골라 실행 (이름 부분일치, 대소문자 무시, 여러 번 지정 가능)
./work/dotnet/dotnet.exe "tests/hoonsoo.Tests/bin/Release/net10.0-windows10.0.19041.0/hoonsoo.Tests.dll" --filter "region"
./work/dotnet/dotnet.exe "tests/hoonsoo.Tests/bin/Release/net10.0-windows10.0.19041.0/hoonsoo.Tests.dll" --list
```

`--filter`를 지정하지 않으면 예전처럼 33개 전체를 실행합니다. 창을 띄우거나 마우스/키를 주입하는 검사(팝업 드래그, 영역 선택 드래그, AR 오버레이, ESC 주입, 실행 파일 교체)는 필터로 제외하세요. `work/probe-ar`는 전체 화면 오버레이를 띄우고 마우스를 주입하므로 다른 작업 중에는 실행하지 마세요.

UIA와 OCR은 별도 hoonsoo 작업 프로세스에서 실행하며, UIA 1.8초/OCR 12초를 초과하거나 요청이 취소되면 해당 작업 프로세스만 종료합니다. 부모/자식 탐색량과 텍스트 크기를 제한합니다. 클립보드는 사용자가 복사를 누를 때만 씁니다.

## 제한

관리자 권한 앱, 보안 데스크톱, DRM 화면, 접근성을 노출하지 않는 앱은 캡처가 제한될 수 있습니다. OCR 모델은 영어 전용이고 작은 글씨·복잡한 레이아웃은 수동 선택이 필요할 수 있습니다. x64 배포이며 Windows ARM64 네이티브 빌드는 제공하지 않습니다. 혼합 DPI 배치는 물리 좌표와 모니터별 DPI로 계산하지만 다양한 실제 멀티모니터 장비에 대한 검증은 별도입니다. 전역 ESC를 소비하지 않으므로 원래 앱에도 ESC가 전달될 수 있습니다.

다크 테마는 앱 창(설정·번역 팝업)에 적용됩니다. 트레이 아이콘 우클릭 메뉴는 Windows 시스템 메뉴라 앱 테마를 따르지 않으며, OS 테마가 라이트이면 다크를 골라도 메뉴는 라이트로 남습니다. 전체 화면 AR 자막과 영역 선택 오버레이는 항상 어두운 화면 위 자막/캡처 UI이므로 테마와 무관하게 고정입니다(가독성 우선). **화면 표시 크기**에서 두 배율을 서로 다르게 고르면 "영역 판 > AR 자막" 관계가 뒤집힐 수 있습니다(기본값 100%/100%에서는 항상 유지).

## 공식 문서 / 라이선스

- [.NET 10 다운로드](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [RegisterHotKey / MOD_NOREPEAT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [Tesseract .NET wrapper / native runtime requirement](https://github.com/charlesw/tesseract)
- [Tesseract OCR / Apache 2.0](https://tesseract-ocr.github.io/tessdoc/Installation.html)
- [Windows OCR package identity restriction](https://github.com/MicrosoftDocs/winrt-api/blob/docs/windows.media.ocr/windows_media_ocr.md) — 패키지 등록 없는 배포를 위해 Tesseract를 선택했습니다.
- [Visual C++ redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)

배포 폴더의 `licenses/`에 Tesseract, wrapper, 영어 학습 데이터(Apache 2.0), Leptonica(BSD 계열) 라이선스를 포함합니다. .NET 런타임의 LICENSE/ThirdPartyNotices도 배포에 포함됩니다. OCR 엔진·학습 데이터는 CPU에서 실행되며 Preview API/NPU를 요구하지 않습니다.
