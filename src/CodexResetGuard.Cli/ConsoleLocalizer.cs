using System.Globalization;
using CodexResetGuard.Core;

namespace CodexResetGuard.Cli;

internal sealed class ConsoleLocalizer
{
    private readonly bool korean;

    public ConsoleLocalizer(UiLanguage language)
    {
        korean = language == UiLanguage.Korean
            || (language == UiLanguage.Auto
                && string.Equals(
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                    "ko",
                    StringComparison.OrdinalIgnoreCase));
    }

    public string Header(bool automationEnabled) => automationEnabled
        ? korean
            ? "CodexResetGuard — 자동 초기화 켜짐"
            : "CodexResetGuard — automation enabled"
        : korean
            ? "CodexResetGuard — 자동 초기화 꺼짐"
            : "CodexResetGuard — automation disabled";

    public string DiagnosticsReadOnly => korean
        ? "진단 모드: 사용량만 조회하며 초기화권을 사용하지 않습니다."
        : "Diagnostics mode: usage is read-only; no reset credit is used.";

    public string Weekly => korean ? "주간 한도" : "Weekly limit";

    public string Unknown => korean ? "확인 불가" : "Unavailable";

    public string Remaining => korean ? "잔여량" : "Remaining";

    public string ResetsAt => korean ? "초기화 시각" : "Resets at";

    public string Threshold => korean ? "임계값" : "Threshold";

    public string Credits => korean ? "초기화권" : "Reset credits";

    public string Decision => korean ? "판단" : "Decision";

    public string AutomationDisabled => korean
        ? "자동 초기화 꺼짐"
        : "Automation disabled";

    public string ServerDeterminedScope => korean
        ? "주간 한도는 발동 조건이며, 실제 초기화 범위는 서버가 결정합니다."
        : "The weekly limit is the trigger; the server determines the actual reset scope.";

    public string LiveDuplicateSuppressed => korean
        ? "같은 주간 구간에 완료 기록이 있어 중복 실행하지 않았습니다."
        : "A terminal record already exists for this weekly window; no duplicate was sent.";

    public string LiveNoAction => korean
        ? "현재 발동 조건을 충족하지 않아 초기화권을 사용하지 않았습니다."
        : "The trigger is not currently met; no reset credit was used.";

    public string LiveNotEnabled => korean
        ? "자동 초기화가 활성화되지 않았습니다."
        : "Automation is not enabled.";

    public string LiveStatePreserved => korean
        ? "초기화 상태는 보존되었습니다. 재시도 시 같은 요청을 이어서 처리합니다."
        : "Reset state was preserved; a retry reconciles the same logical attempt.";

    public string LiveFailureState(LiveResetFailureDisposition disposition) => disposition switch
    {
        LiveResetFailureDisposition.Retryable => LiveStatePreserved,
        LiveResetFailureDisposition.ProtocolMismatch => korean
            ? "보류 중인 초기화 상태는 보존되고 프로토콜 불일치로 차단되었습니다."
            : "Any pending reset state was preserved and blocked for a protocol mismatch.",
        _ => korean
            ? "보류 중인 초기화 상태는 보존되고 검토 필요 상태로 차단되었습니다."
            : "Any pending reset state was preserved and blocked for review.",
    };

    public string RefreshCompleted => korean
        ? "터미널 결과 후 전체 사용량을 다시 조회했습니다."
        : "Completed a full usage refresh after the terminal outcome.";

    public string RefreshPending => korean
        ? "터미널 결과는 저장됐지만 후속 전체 조회는 아직 완료되지 않았습니다."
        : "The terminal outcome is durable, but its follow-up full refresh is still pending.";

    public string LiveOutcome(string outcome) => korean
        ? $"초기화 결과: {outcome}"
        : $"Reset outcome: {outcome}";

    public string LiveBlocked(string reason) => korean
        ? $"자동 초기화가 안전하게 차단되었습니다: {reason}"
        : $"Automation was safely blocked: {reason}";

    public string SettingsCreated => korean
        ? "기본 설정 파일을 만들었습니다."
        : "Created the default settings file.";

    public string AlreadyRunning => korean
        ? "CodexResetGuard가 이미 실행 중입니다."
        : "CodexResetGuard is already running.";

    public string Stopping => korean ? "종료합니다." : "Stopping.";

    public string Failure(string category) => korean
        ? $"안전하게 중단됨: {category}"
        : $"Stopped safely: {category}";

}
