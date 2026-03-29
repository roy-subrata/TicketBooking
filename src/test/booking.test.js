import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';
const SEAT_NUMBER = Number(__ENV.SEAT_NUMBER || 3);
const ATTEMPTS = Number(__ENV.ATTEMPTS || 50);

const bookingSuccesses = new Counter('booking_successes');
const bookingRejected = new Counter('booking_rejected');
const unexpectedResponses = new Counter('unexpected_responses');

export const options = {
    scenarios: {
        race_for_one_seat: {
            executor: 'shared-iterations',
            vus: ATTEMPTS,
            iterations: ATTEMPTS,
            maxDuration: '30s',
        },
    },
    thresholds: {
        booking_successes: ['count==1'],
        unexpected_responses: ['count==0'],
        http_req_failed: ['rate==0'],
    },
};

export function setup() {
    console.log(`Starting concurrency race test for seat ${SEAT_NUMBER} with ${ATTEMPTS} attempts`);
    console.log('Use a fresh available seat before each run, or the result will be misleading.');
}

export default function () {
    const userId = `user-${__VU}-${__ITER}-${Date.now()}`;
    const payload = JSON.stringify({
        seatNumber: SEAT_NUMBER,
        userId,
    });

    const response = http.post(`${BASE_URL}/bookings`, payload, {
        headers: {
            'Content-Type': 'application/json',
        },
        tags: {
            endpoint: 'bookings',
            seat: String(SEAT_NUMBER),
        },
    });

    const ok = check(response, {
        'response is 200 or 400': (r) => r.status === 200 || r.status === 400,
    });

    if (!ok) {
        unexpectedResponses.add(1);
        console.log(`Unexpected response: status=${response.status} body=${response.body}`);
        return;
    }

    if (response.status === 200) {
        bookingSuccesses.add(1);
        console.log(`SUCCESS: VU ${__VU} booked seat ${SEAT_NUMBER} for ${userId}`);
        return;
    }

    bookingRejected.add(1);
    console.log(`REJECTED: VU ${__VU} could not book seat ${SEAT_NUMBER}. body=${response.body}`);
}

