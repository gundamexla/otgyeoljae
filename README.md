# 모아의 결재함

가진 옷은 살리고, 남겨둘 돈은 지키는 AI 쇼핑 비서. OBA.RUN 시즌2 SKT telecom 트랙.

## Agentic Pay 시연 흐름
자유 입력 → A.X K2 의도 해석 → 서버 카탈로그·옷장 조회 → 결재 기준 판정 → 자동 처리 또는 사용자 승인 대기 → 모의 결제 → 결과 조회·영수증 → 피드백을 다음 구매에 적용.

- 면접: 새 재킷 92,000원은 생활비 침범으로 차단. 보유 재킷+물세탁 바지 42,000원을 승인하면 가상 잔액 88,000원.
- 양말·휴지: 기준 안의 익숙한 반복 구매는 모의 자동 처리.
- 결혼식·생신 선물: 예산·환불·신규 구매 등 조건을 보고 결재 또는 차단.
- 중복 구독: 추가 구매 만류. 사칭·상품권 위험 요청: 차단.
- 피드백: 실제 구매 영수증에 남긴 관리·허리 불편 등을 다음 견적의 확인 항목·카테고리 결재 기준에 반영.
- 대안 버튼은 실제 대안 견적을 선택. 서버 가격만 사용하며 승인 직전 정책·가격·재고 재검증.
- 중복 요청·중복 승인 방지. 반려 뒤 결제 금지. 실패·결과 불명 시연과 결과 조회 후 재시도 흐름 지원.

## 실행
Node.js 22 이상. `.env.example`을 `.env`로 복사하고 `OBA_API_KEY` 설정 후 `npm --prefix moa-mobile ci`, `npm run build:mobile`, `npm start`. http://localhost:4400
Vercel은 api/index.mjs + vercel.json 사용. OBA_API_KEY를 서버용 Sensitive 환경변수로 설정.

## 검증·제약
**실제 주문·금융 결제는 하지 않으며 SKT 결제 SDK 연동이 아닙니다.** SKT A.X K2를 사용한 서버 제어형 모의 결제입니다. 상품·옷장·일정은 샘플입니다. 착장 버튼으로 기존 사진 착장 사이트를 열 수 있고, 모의 결제 후 같은 Vercel 주소의 금융 창에 서버 영수증을 표시합니다. 외부 상품몰 주문과 자동 사진 전송은 하지 않습니다.
로컬은 세션별 JSON 저장, Vercel은 인스턴스 /tmp 저장으로 재배포·콜드 스타트 시 초기화될 수 있습니다. 장기 저장 보장이 없습니다. 브라우저별 세션은 분리합니다.
음성 전화·문자·자녀에게 실제 알림·실제 상품 검색·장기 학습은 구현하지 않았습니다. 사진 시착은 별도 착장 사이트에서 사진 선택과 동의 후 진행합니다. 키·개인정보는 저장소에 올리지 않습니다.

## API
GET /api/health, /api/state, /api/jobs/:quoteId
POST /api/agent, /api/quote, /api/approve, /api/feedback, /api/rules, /api/reset
테스트 환경: MOA_DISABLE_LLM=1, MOA_RUNTIME_DIR, MOA_NO_LISTEN=1.

## 팀
허우민(익뇽, gundamexla), SB(LSB04).

## 공개 체험과 모바일 UI

[Vercel 공개 체험](https://otgyeoljae.vercel.app/) · [휴대폰 화면](https://otgyeoljae.vercel.app/mobile/) · [QR PNG](qr/moa-qr.png) · [QR SVG](qr/moa-qr.svg)

휴대폰은 `/mobile/`로 자동 이동합니다. PC와 휴대폰 모두 모아 캐릭터와 착장·금융 버튼을 제공합니다. 모의 결제를 승인하면 `/finance/`에서 상품, 배송비, 결제 금액, 잔액을 확인할 수 있습니다. [Vercel 연결 설명](VERCEL-INTEGRATION.md)과 [모바일 서버 연동](MOBILE-INTEGRATION.md)을 참고하세요.
