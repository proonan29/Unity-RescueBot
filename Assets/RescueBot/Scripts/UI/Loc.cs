using System.Collections.Generic;
using UnityEngine;

namespace RescueBot.UI
{
    /// <summary>Korean/English strings. English text is the key; the choice is stored in PlayerPrefs.</summary>
    public static class Loc
    {
        static bool? korean;
        public static bool Korean
        {
            get { if (korean == null) korean = PlayerPrefs.GetInt("RescueBot.lang", 1) == 1; return korean.Value; }
            set { korean = value; PlayerPrefs.SetInt("RescueBot.lang", value ? 1 : 0); }
        }

        static readonly Dictionary<string, string> ko = new Dictionary<string, string>
        {
            ["Camera-guided disaster rescue rover"] = "카메라 영상으로 판단하는 재난 구조 로봇",
            ["Scene"] = "장면", ["Grid X (blocks)"] = "격자 X (블록)", ["Grid Y (blocks)"] = "격자 Y (블록)",
            ["Road spacing (m)"] = "도로 간격 (m)", ["Disaster severity"] = "재난 강도",
            ["Keep every junction reachable"] = "모든 교차로 도달 가능 보장",
            ["New scene"] = "새 Scene 생성", ["seed"] = "시드", ["city"] = "도시",
            ["Run"] = "실행", ["Start"] = "시작", ["Stop"] = "정지",
            ["Loop (new start and goal after arrival)"] = "Loop (도착하면 새 출발지·목적지로 반복)",
            ["Speed"] = "속도", ["Camera view"] = "카메라 뷰",
            ["Overview"] = "전체", ["Top"] = "탑뷰", ["Chase"] = "추적", ["Onboard"] = "로봇 카메라",
            ["Show route"] = "경로 표시", ["Show decision line"] = "판단 방향 표시",
            ["Legend"] = "범례", ["Road"] = "도로", ["Crossable debris"] = "넘을 수 있는 잔해", ["Solid obstacle"] = "통과 불가 장애물",
            ["Start point"] = "출발지", ["Goal"] = "목적지", ["Route"] = "경로", ["Rover"] = "로봇",
            ["Settings"] = "설정",
            ["Rover camera"] = "로봇 카메라 영상", ["Perception"] = "영상 인식 결과",
            ["road · debris (crossable) · solid · heading"] = "도로 · 잔해(넘을 수 있음) · 장애물 · 선택 방향",
            ["Decision"] = "판단", ["Trip"] = "이동", ["Totals"] = "누적", ["Recent trips"] = "최근 이동",
            ["Go straight"] = "직진", ["Avoid left"] = "좌측 회피", ["Avoid right"] = "우측 회피", ["Cross debris"] = "잔해 통과", ["Turning"] = "방향 전환", ["Blocked"] = "막힘",
            ["Idle"] = "대기", ["Driving"] = "주행 중", ["Arrived"] = "도착", ["Failed"] = "실패",
            ["state"] = "상태", ["action"] = "행동", ["heading"] = "방향", ["clearance"] = "전방 여유", ["speed"] = "속도",
            ["time"] = "시간", ["distance"] = "거리", ["replans"] = "경로 재계획", ["blocked roads found"] = "발견한 막힌 도로",
            ["debris crossed"] = "넘은 잔해", ["collisions"] = "충돌", ["ideal path cost"] = "이상 경로 비용",
            ["efficiency"] = "효율", ["trips"] = "이동 횟수", ["success"] = "성공", ["failed"] = "실패", ["avg time"] = "평균 시간",
            ["obstacles"] = "장애물", ["blocked roads"] = "막힌 도로", ["rough roads"] = "험로",
            ["no route"] = "경로 없음", ["stuck"] = "정체", ["timeout"] = "시간 초과", ["arrived"] = "도착",
            ["Drag: rotate · Right-drag: pan · Wheel: zoom"] = "드래그: 회전 · 오른쪽 드래그: 이동 · 휠: 확대",
            ["Reachable guaranteed"] = "도달 가능 보장",
            ["s"] = "초", ["m"] = "m",
        };

        public static string T(string en) => Korean && ko.TryGetValue(en, out var k) ? k : en;
    }
}
