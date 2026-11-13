# Cloud deployment

## Ροή έκδοσης

- Το `Dockerfile` στη ρίζα χτίζει μόνο το cloud API.
- Το GitHub Actions χτίζει το Docker image και το δημοσιεύει στο GHCR.
- Το `docker-compose.yml` στη ρίζα δεν κάνει build· κάνει pull το image από το GHCR και ξεκινά το API μαζί με την PostgreSQL.
- Push στο `main` φτιάχνει δοκιμαστικό image με tags `main` και commit SHA.
- Git tag έκδοσης όπως `v17.3.113` δημιουργεί τα images `:v17.3.113` και `:latest`. Με ενεργό deployment, το Actions συνδέεται μέσω SSH και αναβαθμίζει τον VPS με το `:latest`.

Κυκλοφόρησε νέα έκδοση από commit του `main`:

```sh
git tag v17.3.113
git push origin v17.3.113
```

Ο αριθμός έκδοσης μπαίνει στο Git tag, όχι στην έκδοση .NET του Dockerfile. Το `latest` σημαίνει την τελευταία έκδοση που προωθήθηκε με version tag.

## Proxy

Δεν περιλαμβάνεται Caddy. Το Compose δημοσιεύει το API από προεπιλογή μόνο στο `127.0.0.1:8080` του VPS. Ο proxy του IT πρέπει να τερματίζει το HTTPS και να προωθεί τα αιτήματα στο `http://127.0.0.1:8080`. Το δημόσιο URL του proxy είναι η διεύθυνση που χρησιμοποιούν ο installer και ο License Manager.

Αν ο proxy τρέχει σε Docker ή σε διαφορετικό server, ρύθμισε πρώτα την προσβάσιμη ιδιωτική διεύθυνση του API και το firewall. Μην ανοίξεις τη θύρα 5432 της PostgreSQL στο Internet.

## Αρχική εγκατάσταση στον VPS

Ο `VPS_DEPLOY_DIR` πρέπει να είναι φάκελος εγγράψιμος από τον SSH χρήστη, για παράδειγμα `/opt/restaurant-orders`. Ο φάκελος περιέχει το `.env` και τον υποφάκελο `secrets/`. Το Actions αντιγράφει εκεί τα `docker-compose.yml`, `.env.example` και το script αρχικοποίησης της βάσης σε κάθε έκδοση.

Αντέγραψε το `.env.example` σε `.env`, κράτησε το `CLOUD_IMAGE_TAG=latest` και πρόσθεσε τέσσερα διαφορετικά μυστικά. Παράδειγμα σε Linux:

```sh
mkdir -p /opt/restaurant-orders/secrets
cd /opt/restaurant-orders
curl -fsSL https://raw.githubusercontent.com/Saranto717/Licence-Rest/main/.env.example -o .env.example
cp .env.example .env
for key in POSTGRES_PASSWORD_SECRET CLOUD_APP_PASSWORD_SECRET CLOUD_ADMIN_KEY_SECRET LICENSE_MANAGER_PASSWORD_SECRET; do
  printf '%s=%s\n' "$key" "$(openssl rand -hex 32)" >> .env
done
chmod 600 .env
mkdir -p secrets
```

Μετέφερε το `license-signing-private.pem` με ασφαλή τρόπο στον `secrets/license-signing-private.pem`. Μην βάλεις το `.env`, το ιδιωτικό κλειδί ή το GitHub token στο repository.

Για ιδιωτικό image, δημιούργησε GitHub Personal Access Token (classic) με μόνο `read:packages`. Κάνε μία φορά σύνδεση στο GHCR από τον VPS, ως τον ίδιο Linux χρήστη που εκτελεί το Compose:

```sh
read -rsp "GitHub read:packages token: " GHCR_TOKEN; echo
printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u YOUR_GITHUB_USERNAME --password-stdin
unset GHCR_TOKEN
```

Πριν ενεργοποιήσεις το αυτόματο deployment, έλεγξε ότι υπάρχουν το `.env`, το αρχείο ιδιωτικού κλειδιού και η σύνδεση GHCR. Για την πρώτη εκκίνηση:

```sh
docker compose --env-file .env pull cloud-api
docker compose --env-file .env up -d
```

## Ρυθμίσεις GitHub για αυτόματο deployment

Στο repository `Saranto717/Licence-Rest`, άνοιξε `Settings → Secrets and variables → Actions`.

Πρόσθεσε repository variable `CLOUD_DEPLOY_ENABLED` με τιμή `true` και τα παρακάτω repository secrets:

| Secret | Περιεχόμενο |
|---|---|
| `VPS_HOST` | Δημόσια IP ή hostname του VPS |
| `VPS_USER` | Linux SSH χρήστης με δικαίωμα εκτέλεσης Docker Compose |
| `VPS_DEPLOY_DIR` | Απόλυτη διαδρομή του φακέλου εγκατάστασης |
| `VPS_SSH_PRIVATE_KEY` | Ιδιωτικό κλειδί για τον SSH χρήστη |
| `VPS_KNOWN_HOSTS` | Καταχώριση host key με fingerprint επιβεβαιωμένο από τον IT manager |

Στον VPS πρέπει να έχει προστεθεί το αντίστοιχο δημόσιο SSH key στο `authorized_keys` του χρήστη. Ενεργοποίησε το `CLOUD_DEPLOY_ENABLED` μόνο αφού είναι έτοιμα τα παραπάνω και ο proxy προωθεί στο API.

Η βάση μένει στο Docker volume `postgres-data` και διατηρείται σε αναβαθμίσεις container. Κράτα ξεχωριστά αντίγραφα ασφαλείας.

