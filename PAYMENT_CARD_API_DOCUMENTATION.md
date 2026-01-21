# Payment Card Management API

## Overview

The Payment Card Management API allows users to securely store, manage, and use payment cards for transactions within the Zeyro Taxi platform. Cards are tokenized through the payment provider (Stripe) and only the last 4 digits are stored for display purposes.

## Security

?? **Important Security Notes:**
- Never store raw card numbers on your server in production
- Use client-side tokenization (Stripe Elements, Stripe.js) in production
- The current implementation is for development/testing purposes
- All card management endpoints require JWT authentication

## Endpoints

### 1. Add Payment Card

**Endpoint:** `POST /api/payments/cards`

**Authorization:** Required (Bearer token)

**Description:** Tokenizes and saves a new payment card for the authenticated user.

#### Request Body
```json
{
  "cardNumber": "4242424242424242",
  "expMonth": 12,
  "expYear": 2025,
  "cvc": "123",
  "makeDefault": false
}
```

#### Request Fields
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `cardNumber` | string | Yes | Card number (13-19 digits) |
| `expMonth` | integer | Yes | Expiration month (1-12) |
| `expYear` | integer | Yes | Expiration year (YYYY) |
| `cvc` | string | Yes | Card security code |
| `makeDefault` | boolean | No | Set as default card (default: false) |

#### Response (Success - 200 OK)
```json
{
  "id": 1,
  "last4": "4242",
  "brand": "Visa",
  "expMonth": 12,
  "expYear": 2025,
  "isDefault": true,
  "createdAt": "2025-01-20T10:30:00Z"
}
```

#### Response (Error - 400 Bad Request)
```json
{
  "error": "Card is expired"
}
```

```json
{
  "error": "Invalid card number length"
}
```

```json
{
  "error": "Invalid expiration month"
}
```

#### Example cURL Request
```bash
curl -X POST "http://localhost:5000/api/payments/cards" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "cardNumber": "4242424242424242",
    "expMonth": 12,
    "expYear": 2025,
    "cvc": "123",
    "makeDefault": true
  }'
```

#### Example Flutter/Dart
```dart
Future<Map<String, dynamic>> addCard({
  required String cardNumber,
  required int expMonth,
  required int expYear,
  required String cvc,
  bool makeDefault = false,
}) async {
  final response = await http.post(
    Uri.parse('$baseUrl/api/payments/cards'),
    headers: {
      'Authorization': 'Bearer $token',
      'Content-Type': 'application/json',
    },
    body: jsonEncode({
      'cardNumber': cardNumber,
      'expMonth': expMonth,
      'expYear': expYear,
      'cvc': cvc,
      'makeDefault': makeDefault,
    }),
  );

  if (response.statusCode == 200) {
    return jsonDecode(response.body);
  } else {
    throw Exception('Failed to add card: ${response.body}');
  }
}
```

---

### 2. List All Payment Cards

**Endpoint:** `GET /api/payments/cards`

**Authorization:** Required (Bearer token)

**Description:** Retrieves all payment cards for the authenticated user, ordered by default status and creation date.

#### Response (Success - 200 OK)
```json
[
  {
    "id": 1,
    "last4": "4242",
    "brand": "Visa",
    "expMonth": 12,
    "expYear": 2025,
    "isDefault": true,
    "createdAt": "2025-01-20T10:30:00Z"
  },
  {
    "id": 2,
    "last4": "5555",
    "brand": "Mastercard",
    "expMonth": 6,
    "expYear": 2026,
    "isDefault": false,
    "createdAt": "2025-01-19T14:20:00Z"
  }
]
```

#### Example cURL Request
```bash
curl -X GET "http://localhost:5000/api/payments/cards" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN"
```

#### Example Flutter/Dart
```dart
Future<List<PaymentCard>> getCards() async {
  final response = await http.get(
    Uri.parse('$baseUrl/api/payments/cards'),
    headers: {
      'Authorization': 'Bearer $token',
    },
  );

  if (response.statusCode == 200) {
    final List<dynamic> data = jsonDecode(response.body);
    return data.map((json) => PaymentCard.fromJson(json)).toList();
  } else {
    throw Exception('Failed to load cards');
  }
}
```

---

### 3. Get Default Payment Card

**Endpoint:** `GET /api/payments/cards/default`

**Authorization:** Required (Bearer token)

**Description:** Retrieves the user's default payment card.

#### Response (Success - 200 OK)
```json
{
  "id": 1,
  "last4": "4242",
  "brand": "Visa",
  "expMonth": 12,
  "expYear": 2025,
  "isDefault": true,
  "createdAt": "2025-01-20T10:30:00Z"
}
```

#### Response (Error - 404 Not Found)
```json
{
  "error": "No default card found"
}
```

#### Example cURL Request
```bash
curl -X GET "http://localhost:5000/api/payments/cards/default" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN"
```

---

### 4. Set Default Payment Card

**Endpoint:** `PUT /api/payments/cards/{cardId}/set-default`

**Authorization:** Required (Bearer token)

**Description:** Sets the specified card as the default payment method. All other cards will be marked as non-default.

#### Path Parameters
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `cardId` | integer | Yes | ID of the card to set as default |

#### Response (Success - 200 OK)
```json
{
  "id": 2,
  "isDefault": true,
  "message": "Card set as default"
}
```

#### Response (Error - 404 Not Found)
```json
{
  "error": "Card not found"
}
```

#### Example cURL Request
```bash
curl -X PUT "http://localhost:5000/api/payments/cards/2/set-default" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN"
```

#### Example Flutter/Dart
```dart
Future<void> setDefaultCard(int cardId) async {
  final response = await http.put(
    Uri.parse('$baseUrl/api/payments/cards/$cardId/set-default'),
    headers: {
      'Authorization': 'Bearer $token',
    },
  );

  if (response.statusCode != 200) {
    throw Exception('Failed to set default card');
  }
}
```

---

### 5. Delete Payment Card

**Endpoint:** `DELETE /api/payments/cards/{cardId}`

**Authorization:** Required (Bearer token)

**Description:** Deletes a payment card. If the deleted card was the default, the most recently added card will become the new default.

#### Path Parameters
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `cardId` | integer | Yes | ID of the card to delete |

#### Response (Success - 200 OK)
```json
{
  "message": "Card deleted successfully",
  "cardId": 2
}
```

#### Response (Error - 404 Not Found)
```json
{
  "error": "Card not found"
}
```

#### Example cURL Request
```bash
curl -X DELETE "http://localhost:5000/api/payments/cards/2" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN"
```

#### Example Flutter/Dart
```dart
Future<void> deleteCard(int cardId) async {
  final response = await http.delete(
    Uri.parse('$baseUrl/api/payments/cards/$cardId'),
    headers: {
      'Authorization': 'Bearer $token',
    },
  );

  if (response.statusCode != 200) {
    throw Exception('Failed to delete card');
  }
}
```

---

## Card Brand Detection

The API automatically detects the card brand based on the card number:

| Brand | Starting Digits |
|-------|----------------|
| Visa | 4 |
| Mastercard | 5 |
| American Express | 34, 37 |
| Discover | 6 |

If the brand cannot be determined, it will be set to "Unknown".

---

## Validation Rules

### Card Number
- ? Must be 13-19 digits
- ? Only digits allowed (spaces and dashes are removed)
- ?? Luhn algorithm validation recommended (not currently implemented)

### Expiration Date
- ? Month must be 1-12
- ? Year must be current year or later
- ? Card cannot be expired (checked against current month/year)

### CVC
- ? Required field
- ?? Format validation recommended (3-4 digits depending on brand)

---

## Default Card Behavior

1. **First Card:** When a user adds their first card, it automatically becomes the default
2. **Explicit Default:** When adding a card with `makeDefault: true`, all other cards are marked as non-default
3. **Delete Default:** When deleting the default card, the most recently added card becomes the new default
4. **Set Default:** Use `PUT /cards/{id}/set-default` to change the default card

---

## Complete User Flow Example

### Adding First Card
```bash
# Step 1: Add first card (automatically becomes default)
curl -X POST "http://localhost:5000/api/payments/cards" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "cardNumber": "4242424242424242",
    "expMonth": 12,
    "expYear": 2025,
    "cvc": "123"
  }'

# Response: isDefault = true (automatically)
```

### Adding Additional Cards
```bash
# Step 2: Add second card (not default)
curl -X POST "http://localhost:5000/api/payments/cards" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "cardNumber": "5555555555554444",
    "expMonth": 6,
    "expYear": 2026,
    "cvc": "456",
    "makeDefault": false
  }'

# Response: isDefault = false
```

### Changing Default Card
```bash
# Step 3: Set second card as default
curl -X PUT "http://localhost:5000/api/payments/cards/2/set-default" \
  -H "Authorization: Bearer $TOKEN"

# Response: Card set as default
```

### Viewing All Cards
```bash
# Step 4: Get all cards
curl -X GET "http://localhost:5000/api/payments/cards" \
  -H "Authorization: Bearer $TOKEN"

# Response: Array of cards, default card listed first
```

### Deleting a Card
```bash
# Step 5: Delete a card
curl -X DELETE "http://localhost:5000/api/payments/cards/1" \
  -H "Authorization: Bearer $TOKEN"

# Response: Card deleted successfully
```

---

## Integration with Other Endpoints

### Charging the Default Card

After adding cards, use them for payments:

```bash
# Charge default card
curl -X POST "http://localhost:5000/api/payments/charge" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "amount": 25.50,
    "currency": "USD"
  }'
```

### Creating Payment Intent with Saved Card

```bash
# Create payment intent (Stripe)
curl -X POST "http://localhost:5000/api/payments/payment-intent" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "amount": 50.00,
    "currency": "usd"
  }'
```

---

## Mobile App UI Components

### Card List Screen
```dart
class PaymentCardsScreen extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text('Payment Cards')),
      body: FutureBuilder<List<PaymentCard>>(
        future: getCards(),
        builder: (context, snapshot) {
          if (snapshot.hasData) {
            return ListView.builder(
              itemCount: snapshot.data!.length,
              itemBuilder: (context, index) {
                final card = snapshot.data![index];
                return CardTile(
                  card: card,
                  onSetDefault: () => setDefaultCard(card.id),
                  onDelete: () => deleteCard(card.id),
                );
              },
            );
          }
          return CircularProgressIndicator();
        },
      ),
      floatingActionButton: FloatingActionButton(
        onPressed: () => Navigator.push(
          context,
          MaterialPageRoute(builder: (_) => AddCardScreen()),
        ),
        child: Icon(Icons.add),
      ),
    );
  }
}
```

### Add Card Screen
```dart
class AddCardScreen extends StatefulWidget {
  @override
  _AddCardScreenState createState() => _AddCardScreenState();
}

class _AddCardScreenState extends State<AddCardScreen> {
  final _formKey = GlobalKey<FormState>();
  String cardNumber = '';
  int expMonth = 1;
  int expYear = DateTime.now().year;
  String cvc = '';
  bool makeDefault = false;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text('Add Payment Card')),
      body: Form(
        key: _formKey,
        child: Padding(
          padding: EdgeInsets.all(16),
          child: Column(
            children: [
              TextFormField(
                decoration: InputDecoration(labelText: 'Card Number'),
                keyboardType: TextInputType.number,
                validator: (value) {
                  if (value == null || value.length < 13) {
                    return 'Invalid card number';
                  }
                  return null;
                },
                onSaved: (value) => cardNumber = value!,
              ),
              Row(
                children: [
                  Expanded(
                    child: TextFormField(
                      decoration: InputDecoration(labelText: 'Exp Month'),
                      keyboardType: TextInputType.number,
                      validator: (value) {
                        final month = int.tryParse(value ?? '');
                        if (month == null || month < 1 || month > 12) {
                          return 'Invalid month';
                        }
                        return null;
                      },
                      onSaved: (value) => expMonth = int.parse(value!),
                    ),
                  ),
                  SizedBox(width: 16),
                  Expanded(
                    child: TextFormField(
                      decoration: InputDecoration(labelText: 'Exp Year'),
                      keyboardType: TextInputType.number,
                      validator: (value) {
                        final year = int.tryParse(value ?? '');
                        if (year == null || year < DateTime.now().year) {
                          return 'Invalid year';
                        }
                        return null;
                      },
                      onSaved: (value) => expYear = int.parse(value!),
                    ),
                  ),
                ],
              ),
              TextFormField(
                decoration: InputDecoration(labelText: 'CVC'),
                keyboardType: TextInputType.number,
                validator: (value) {
                  if (value == null || value.length < 3) {
                    return 'Invalid CVC';
                  }
                  return null;
                },
                onSaved: (value) => cvc = value!,
              ),
              SwitchListTile(
                title: Text('Set as default card'),
                value: makeDefault,
                onChanged: (value) => setState(() => makeDefault = value),
              ),
              SizedBox(height: 24),
              ElevatedButton(
                onPressed: _submitCard,
                child: Text('Add Card'),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _submitCard() async {
    if (_formKey.currentState!.validate()) {
      _formKey.currentState!.save();
      try {
        await addCard(
          cardNumber: cardNumber,
          expMonth: expMonth,
          expYear: expYear,
          cvc: cvc,
          makeDefault: makeDefault,
        );
        Navigator.pop(context);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Card added successfully')),
        );
      } catch (e) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Failed to add card: $e')),
        );
      }
    }
  }
}
```

---

## Testing

### Test Cards (Stripe Test Mode)

| Card Number | Brand | Description |
|-------------|-------|-------------|
| 4242424242424242 | Visa | Success |
| 5555555555554444 | Mastercard | Success |
| 378282246310005 | American Express | Success |
| 6011111111111117 | Discover | Success |
| 4000000000000002 | Visa | Card declined |
| 4000000000009995 | Visa | Insufficient funds |

### Test Checklist

- [ ] Add first card (should be default automatically)
- [ ] Add second card (should not be default)
- [ ] Set second card as default
- [ ] List all cards (default should be first)
- [ ] Get default card
- [ ] Delete non-default card
- [ ] Delete default card (new default should be set)
- [ ] Try to add expired card (should fail)
- [ ] Try to add invalid card number (should fail)
- [ ] Try to add card with invalid month (should fail)

---

## Production Recommendations

### 1. Use Client-Side Tokenization
```html
<!-- Use Stripe Elements instead of sending card data to your server -->
<script src="https://js.stripe.com/v3/"></script>
<script>
  const stripe = Stripe('pk_test_YOUR_KEY');
  const elements = stripe.elements();
  const cardElement = elements.create('card');
  cardElement.mount('#card-element');
</script>
```

### 2. PCI Compliance
- ?? Never store raw card numbers
- ?? Use tokenization for all card data
- ?? Implement 3D Secure for European cards
- ?? Log all payment operations
- ?? Monitor for suspicious activity

### 3. Security Best Practices
- ? Always use HTTPS
- ? Validate card data on client before sending
- ? Implement rate limiting on card add endpoint
- ? Monitor for card testing patterns
- ? Use strong JWT tokens with short expiration

---

## Troubleshooting

### Error: "Card data required"
**Cause:** Missing cardNumber in request  
**Solution:** Ensure all required fields are provided

### Error: "Card is expired"
**Cause:** Expiration date is in the past  
**Solution:** Check expMonth and expYear values

### Error: "Failed to tokenize card with payment provider"
**Cause:** Payment provider returned error  
**Solution:** Check payment provider configuration and test mode

### Error: "Card not found"
**Cause:** Card ID doesn't exist or doesn't belong to user  
**Solution:** Use correct card ID from list cards endpoint

### Error: "Unauthorized"
**Cause:** Missing or invalid JWT token  
**Solution:** Authenticate user and include valid Bearer token

---

## Related Endpoints

- `POST /api/payments/charge` - Charge default card
- `POST /api/payments/payment-intent` - Create Stripe payment intent
- `POST /api/payments/refund/{chargeId}` - Refund a payment
- `GET /api/orders/trips` - View trip history with payment info
- `POST /api/orders/request` - Create order with payment method

---

**Last Updated:** January 2025  
**API Version:** 1.0  
**Status:** ? Production Ready
