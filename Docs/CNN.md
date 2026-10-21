# CNN 인식 모듈 (U-Net) — 추가 구현 기록

기존의 색상 규칙 분류기(고전) 대신 CNN으로 카메라 영상을 픽셀 단위로 분류한다. 판단/경로 로직은 그대로 두고 인식 부분만 교체 가능한 구조(`ISegmenter`)로 만들었다.

## 구조
- `Core/Perception.cs`: `ISegmenter` 인터페이스 + `Perception.Segmenter`(static). null이면 고전 `ClassOf` 규칙, 아니면 CNN.
- `Core/CnnSegmenter.cs`: `Resources/rescue_seg.onnx`를 Unity Inference Engine(`com.unity.ai.inference` 2.6.1)으로 실행. GPUCompute 약 2.4 ms/프레임. `PerceptionSwitch`로 전환/상태 표시.
- UI: "영상 인식 모델" 섹션에서 `고전(색상 규칙)` / `CNN (U-Net)` 선택. CLI: `rb_set --perception cnn|classic`, `rb_sim_run --perception cnn|classic`.
- 분류 클래스: 0 도로, 1 넘을 수 있는 잔해, 2 그 외(통과 불가).

## 학습 파이프라인
1. `rb_dump_dataset`(Editor/DatasetCommands.cs): 로봇 카메라 영상(img/)과 정답 라벨(lbl/) 생성. 라벨은 모든 렌더러의 머티리얼을 오브젝트 종류별 단색 unlit으로 바꿔 렌더링한다(색상 규칙으로 만든 라벨이 아니다).
2. `Tools/cnn/train_cnn.py`: U-Net(약 25.6만 파라미터), PyTorch cu126, RTX 4090. ONNX opset 17로 export.
3. 학습 데이터의 조명은 시드마다 무작위(`--light 1`), 검증은 표준 조명(`val_std`).

## 교훈
- **조명 누수**: 테스트 하네스가 열려 있는 씬의 조명에 의존하면 결과가 달라진다. `Harness`가 자체 태양광 + 평탄 ambient를 만들고 다른 CityView/Light를 끄도록 해서 씬과 무관하게 만들었다. (잘못된 조명에서 학습·평가하면 수치가 오해를 부른다.)
- **라벨 렌더링**: 그림자를 끄고 배경은 Other로. 알베도 기반이 아니라 이름 기반 클래스 지정.
- **Unity 에디터 파일 반영**: 커밋과 빌드를 같은 배치에서 돌리지 않는다. PC에 반영된 파일은 MD5로 확인한 뒤 컴파일한다.

## 평가 결과
### 픽셀 정확도
| 조건 | 고전 | CNN |
|---|---|---|
| 표준 조명 | 약 99.99% | 약 99.99% |
| 어둡고 푸른 조명 | 43% | 99.6% |

### 주행 비교 (5x5, 강도 0.5, 시드당 8회 이동, 동일한 도시)
| 시드 | 방식 | 도착 | 효율 | 충돌 |
|---|---|---|---|---|
| 1 | 고전 | 6/8 | 59% | 10 |
| 1 | CNN | 7/8 | 49% | 7 |
| 2 | 고전 | 7/8 | 65% | 9 |
| 2 | CNN | 8/8 | 73% | 3 |
| 3 | 고전 | 8/8 | 69% | 4 |
| 3 | CNN | 8/8 | 65% | 3 |
| **합계** | 고전 | 21/24 | 평균 64% | 23 |
| **합계** | CNN | 23/24 | 평균 62% | 13 |

해석: 표준 조명에서 CNN은 고전과 대등하게 동작하며 도착 수는 약간 많고 충돌은 크게 줄었다(23 → 13). 경로 효율은 비슷하다(표본이 작아 차이는 유의하지 않음). 조명이 바뀌면 고전 방식은 무너지지만 CNN은 유지된다. 시뮬레이션 속도는 CNN이 약 ×50, 고전이 약 ×92로 GPU 추론 비용이 있다.

## 재현
```
rb_dump_dataset --seed N --light 1 --out Dataset/train     # 데이터 생성 (gen_dataset.ps1)
python Tools/cnn/train_cnn.py                              # 학습 + ONNX export
rb_sim_run --seed 1 --severity 0.5 --perception cnn        # 평가 (AgentTools/cmp.ps1로 고전과 비교)
```
`Dataset/`은 용량이 커서 git에서 제외한다.
