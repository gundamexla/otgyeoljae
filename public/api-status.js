export function requestStatusLabel(execution, prefix = '이번 요청') {
  if (!execution) return `${prefix}: 아직 호출하지 않음`;
  if (execution.status === 'running') return `${prefix}: API 호출 중`;
  if (execution.status === 'not_configured') return `${prefix}: 키 없음 · 샘플 응답`;
  if (execution.status === 'fallback') return `${prefix}: API 호출 실패 · 샘플 응답`;
  if (execution.status === 'success') {
    const failed = execution.attempts.filter(a => a.status === 'error').map(a => a.provider);
    return `${prefix}: ${execution.provider} ${failed.length ? '대체 호출 성공 (' + failed.join(', ') + ' 실패)' : '호출 성공'}`;
  }
  return `${prefix}: 결과 확인 필요`;
}

export function apiStatusLabel(llm) {
  const configured = [llm.skt && 'SKT A.X K2', llm.openai && 'OpenAI'].filter(Boolean);
  const setting = configured.length ? `${configured.join(' / ')} 키 설정됨` : 'LLM 키 없음';
  return `${setting} · ${requestStatusLabel(llm.lastRequest, '최근 요청')}`;
}
