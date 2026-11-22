namespace JournalDesk;

internal static class EngineDiagnostic
{
    // Existing read-only indices store these engine-authored diagnostics in Korean.
    internal static string Display(string message)
    {
        if (L.Code == "ko")
            return message;
        const string versionPrefix = "지원하지 않는 USN V";
        if (message.StartsWith(versionPrefix, StringComparison.Ordinal) &&
            ushort.TryParse(message.AsSpan(versionPrefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return L.T("지원하지 않는 USN V{0}", "Unsupported USN V{0}", message[versionPrefix.Length..]);
        return message switch
        {
            "손상 또는 잘린 데이터" => L.T(message, "Damaged or truncated data"),
            "NTFS 트랜잭션 로그의 헤더 또는 레코드를 찾을 수 없습니다." => L.T(message, "No NTFS transaction log headers or records were found."),
            "$MFT의 첫 1 MiB에서 원본 위치와 형식이 일치하는 FILE 레코드를 찾을 수 없습니다." => L.T(message, "No FILE records with valid offsets and format were found in the first 1 MiB of $MFT."),
            "분석 중 원본 파일 크기가 변경되었습니다." => L.T(message, "The source file size changed during analysis."),
            "FILE 서명 없음" => L.T(message, "Missing FILE signature"),
            "레코드 크기 불일치" => L.T(message, "Record size mismatch"),
            "레코드 헤더 범위 오류" => L.T(message, "Record header out of bounds"),
            "원본 위치와 엔트리 번호 불일치" => L.T(message, "Source offset and entry number do not match"),
            "섹터 끝의 업데이트 시퀀스 불일치" => L.T(message, "Update sequence mismatch at sector boundary"),
            "파일명 속성 잘림" => L.T(message, "Truncated file name attribute"),
            "파일명 길이 또는 네임스페이스 오류" => L.T(message, "Invalid file name length or namespace"),
            "파일명 데이터 잘림" => L.T(message, "Truncated file name data"),
            "파일명 UTF-16 오류" => L.T(message, "Invalid UTF-16 in file name"),
            "파일명에 잘못된 경로 구분자가 있음" => L.T(message, "Invalid path separator in file name"),
            "속성 목록 헤더 잘림" => L.T(message, "Truncated attribute list header"),
            "속성 목록 길이 오류" => L.T(message, "Invalid attribute list length"),
            "속성 종료 표지 없음" => L.T(message, "Missing attribute end marker"),
            "속성 헤더 잘림" => L.T(message, "Truncated attribute header"),
            "속성 길이 또는 저장 형식 오류" => L.T(message, "Invalid attribute length or storage format"),
            "상주 속성 데이터 범위 오류" => L.T(message, "Resident attribute data out of bounds"),
            "파일명 속성 인스턴스 번호 중복" => L.T(message, "Duplicate file name attribute instance number"),
            "마지막 레코드 잘림" => L.T(message, "Truncated final record"),
            _ => message
        };
    }
}
