import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
    scenarios: {
        booking_stress: {
            executor: 'constant-vus',
            vus: 50,           // 50 concurrent users (increase for more stress)
            duration: '30s',   // run for 30 seconds
        },
    },
};

export default function () {
    const payload = JSON.stringify({
        ticketId: 1,                    // ← same ticket for all → high contention
        userId: `user-${__VU}-${Date.now()}`,
    });

    const params = {
        headers: { 'Content-Type': 'application/json' },
    };

    const res = http.post(`${__ENV.API_URL}/bookings`, payload, params);

    check(res, {
        'is 200 (success) or 409 (conflict)': (r) => r.status === 200 || r.status === 409,
        'response time < 500ms': (r) => r.timings.duration < 500,
    });

    // Optional: log conflicts so you can see optimistic locking in action
    if (res.status === 409) {
        console.log(`VU ${__VU} got conflict → another instance booked it first!`);
    }

    sleep(0.1);   // small think time (remove for maximum stress)
}