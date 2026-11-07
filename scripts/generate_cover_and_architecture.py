import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUT_DIR = "Mostaql_Portfolio_PulseReserve"
os.makedirs(OUT_DIR, exist_ok=True)

# Fonts
font_title = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 68)
font_subtitle = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 26)
font_badge = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 18)
font_tech = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 20)
font_bullet_title = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 22)
font_bullet_desc = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 18)
font_small = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 15)

def create_cover():
    width, height = 1920, 1080
    im = Image.new("RGBA", (width, height), (11, 15, 25, 255))
    draw = ImageDraw.Draw(im)

    # Subtle background glowing gradients
    # Radial glows (top left cyan, bottom right purple)
    glow_cyan = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    glow_cyan_draw = ImageDraw.Draw(glow_cyan)
    glow_cyan_draw.ellipse([(-200, -200), (800, 800)], fill=(6, 182, 212, 45))
    glow_cyan = glow_cyan.filter(ImageFilter.GaussianBlur(140))
    im = Image.alpha_composite(im, glow_cyan)

    glow_purple = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    glow_purple_draw = ImageDraw.Draw(glow_purple)
    glow_purple_draw.ellipse([(1100, 400), (2100, 1400)], fill=(139, 92, 246, 35))
    glow_purple = glow_purple.filter(ImageFilter.GaussianBlur(150))
    im = Image.alpha_composite(im, glow_purple)

    draw = ImageDraw.Draw(im)

    # Top Category Pill
    pill_text = "ENTERPRISE TICKETING & FLASH-SALE ENGINE"
    draw.rounded_rectangle([90, 85, 540, 125], radius=20, fill=(15, 23, 42, 220), outline=(6, 182, 212, 180), width=2)
    draw.text((115, 93), pill_text, fill=(34, 211, 238), font=font_badge)

    # Project Title
    draw.text((90, 145), "PulseReserve", fill=(255, 255, 255), font=font_title)
    draw.text((90, 230), "High-Concurrency Seat Reservation & Flash-Sale Platform", fill=(148, 163, 184), font=font_subtitle)

    # Key Architectural Pillars / Highlights
    features = [
        ("⚡ Zero Double-Booking Engine", "Authoritative PostgreSQL conditional locking via FOR UPDATE SKIP LOCKED"),
        ("🏗️ Modular Monolith Architecture", "Strict bounded contexts, decoupled schema-per-module isolation in .NET 10"),
        ("🔴 Real-Time Live Seat Map", "Sub-second seat reservation sync & instant lock release using SignalR"),
        ("💳 Enterprise Payments & Outbox", "Transactional Outbox pattern, automated reconciliation & multi-provider (Stripe/Paymob)"),
        ("🛡️ High Engineering Standards", "100% boundary isolation verified by NetArchTest & disposable Testcontainers"),
    ]

    fy = 300
    for title, desc in features:
        # Card item
        draw.rounded_rectangle([90, fy, 850, fy + 72], radius=12, fill=(19, 26, 43, 200), outline=(51, 65, 85, 120), width=1)
        # Left accent line
        draw.rounded_rectangle([90, fy, 95, fy + 72], radius=3, fill=(6, 182, 212, 255))
        draw.text((115, fy + 12), title, fill=(241, 245, 249), font=font_bullet_title)
        draw.text((115, fy + 40), desc, fill=(148, 163, 184), font=font_bullet_desc)
        fy += 86

    # Tech Stack Badges at Bottom Left
    ty = fy + 30
    draw.text((90, ty), "TECH STACK & ARCHITECTURE:", fill=(100, 116, 139), font=font_small)
    ty += 28
    tech_tags = [
        (".NET 10", (59, 130, 246)),
        ("C# 13", (147, 51, 234)),
        ("PostgreSQL", (51, 103, 145)),
        ("Angular 22", (239, 68, 68)),
        ("SignalR", (16, 185, 129)),
        ("Modular Monolith", (217, 119, 6)),
        ("Clean Architecture", (14, 165, 233)),
        ("Docker", (37, 99, 235))
    ]

    bx = 90
    for tag, color in tech_tags:
        bbox = font_tech.getbbox(tag)
        tw = bbox[2] - bbox[0] + 28
        draw.rounded_rectangle([bx, ty, bx + tw, ty + 36], radius=8, fill=(15, 23, 42, 240), outline=color, width=1)
        draw.text((bx + 14, ty + 6), tag, fill=(248, 250, 252), font=font_tech)
        bx += tw + 12
        if bx > 800:
            bx = 90
            ty += 46

    # Right side: Screenshots Mockup in sleek stylized frames
    seat_img_path = os.path.join(OUT_DIR, "02_Interactive_Seat_Map_Live_Booking.png")
    admin_img_path = os.path.join(OUT_DIR, "03_Admin_Financial_Ops_Dashboard.png")

    if os.path.exists(seat_img_path):
        screen1 = Image.open(seat_img_path).convert("RGBA")
        # Resize to fit in mockup frame
        frame1_w, frame1_h = 960, 560
        screen1_resized = screen1.resize((frame1_w, frame1_h), Image.Resampling.LANCZOS)

        # Mockup window container
        mock1 = Image.new("RGBA", (frame1_w, frame1_h + 38), (17, 24, 39, 255))
        m1_draw = ImageDraw.Draw(mock1)
        # Window Header
        m1_draw.rectangle([0, 0, frame1_w, 38], fill=(30, 41, 59, 255))
        # Window controls
        m1_draw.ellipse([14, 13, 24, 23], fill=(239, 68, 68, 255))
        m1_draw.ellipse([32, 13, 42, 23], fill=(245, 158, 11, 255))
        m1_draw.ellipse([50, 13, 60, 23], fill=(16, 185, 129, 255))
        m1_draw.text((75, 11), "PulseReserve — Interactive Live Seat Map", fill=(148, 163, 184), font=font_small)

        mock1.paste(screen1_resized, (0, 38))

        # Add drop shadow
        shadow1 = Image.new("RGBA", (frame1_w + 60, frame1_h + 98), (0, 0, 0, 0))
        sh_draw = ImageDraw.Draw(shadow1)
        sh_draw.rounded_rectangle([30, 30, frame1_w + 30, frame1_h + 68], radius=16, fill=(0, 0, 0, 160))
        shadow1 = shadow1.filter(ImageFilter.GaussianBlur(25))

        im.paste(shadow1, (880, 80), shadow1)
        im.paste(mock1, (910, 110))

    if os.path.exists(admin_img_path):
        screen2 = Image.open(admin_img_path).convert("RGBA")
        frame2_w, frame2_h = 900, 450
        screen2_resized = screen2.resize((frame2_w, frame2_h), Image.Resampling.LANCZOS)

        mock2 = Image.new("RGBA", (frame2_w, frame2_h + 38), (17, 24, 39, 255))
        m2_draw = ImageDraw.Draw(mock2)
        m2_draw.rectangle([0, 0, frame2_w, 38], fill=(30, 41, 59, 255))
        m2_draw.ellipse([14, 13, 24, 23], fill=(239, 68, 68, 255))
        m2_draw.ellipse([32, 13, 42, 23], fill=(245, 158, 11, 255))
        m2_draw.ellipse([50, 13, 60, 23], fill=(16, 185, 129, 255))
        m2_draw.text((75, 11), "PulseReserve — Admin Financial & Ops Center", fill=(148, 163, 184), font=font_small)

        mock2.paste(screen2_resized, (0, 38))

        shadow2 = Image.new("RGBA", (frame2_w + 60, frame2_h + 98), (0, 0, 0, 0))
        sh_draw2 = ImageDraw.Draw(shadow2)
        sh_draw2.rounded_rectangle([30, 30, frame2_w + 30, frame2_h + 68], radius=16, fill=(0, 0, 0, 190))
        shadow2 = shadow2.filter(ImageFilter.GaussianBlur(30))

        im.paste(shadow2, (950, 520), shadow2)
        im.paste(mock2, (980, 550))

    # Watermark / Verification Pill (No contact info, pure portfolio badge)
    draw = ImageDraw.Draw(im)
    draw.rounded_rectangle([width - 320, height - 70, width - 90, height - 30], radius=10, fill=(15, 23, 42, 220), outline=(51, 65, 85, 150), width=1)
    draw.text((width - 300, height - 58), "✓ VERIFIED PORTFOLIO PROJECT", fill=(16, 185, 129), font=font_badge)

    im.convert("RGB").save(os.path.join(OUT_DIR, "00_Cover_Thumbnail_Mostaql_1920x1080.png"), quality=95)
    print("Cover image saved successfully!")

def create_architecture_diagram():
    width, height = 1920, 1080
    im = Image.new("RGBA", (width, height), (15, 23, 42, 255))
    draw = ImageDraw.Draw(im)

    # Title & Subtitle
    draw.text((80, 50), "PulseReserve — Modular Monolith System Architecture", fill=(255, 255, 255), font=font_title)
    draw.text((80, 130), "Production Architecture & High-Concurrency Data Flow Blueprint", fill=(148, 163, 184), font=font_subtitle)

    # 1. Top Presentation & API Host Layer
    draw.rounded_rectangle([80, 190, 1840, 310], radius=16, fill=(30, 41, 59, 230), outline=(6, 182, 212, 180), width=2)
    draw.text((110, 205), "PULSERESERVE.APPHOST  (ASP.NET Core 10 Minimal APIs & Real-Time SignalR Hub)", fill=(34, 211, 238), font=font_bullet_title)

    client_boxes = [
        ("Angular 22 SPA (Client)", "Event catalog, interactive seat selection, booking flow, ticket view", 110, 245, 520),
        ("Admin Ops Console (SPA)", "Reconciliation tasks, manual refunds, outbox monitor, anomaly detection", 660, 245, 530),
        ("SignalR Live Hub (/hubs/live)", "Real-time seat state broadcast, instant lock feedback, live countdown", 1220, 245, 590),
    ]
    for ctitle, cdesc, bx, by, bw in client_boxes:
        draw.rounded_rectangle([bx, by, bx + bw, by + 50], radius=8, fill=(15, 23, 42, 200), outline=(71, 85, 105, 150), width=1)
        draw.text((bx + 14, by + 7), ctitle, fill=(248, 250, 252), font=font_tech)
        draw.text((bx + 14, by + 28), cdesc, fill=(148, 163, 184), font=font_small)

    # 2. Bounded Contexts (Vertical Slices)
    modules = [
        ("Identity Module", "PulseReserve.Modules.Identity", ["User Credentials & Argon2id", "HttpOnly Session Cookies", "Admin Authorization Guard", "Zero Password Leakage"], 80, 350, 320),
        ("Catalog Module", "PulseReserve.Modules.Catalog", ["Venues, Layouts & Tiers", "Dynamic Event Pricing", "Optimistic Concurrency Version", "Composite Foreign Keys"], 430, 350, 320),
        ("Reservation Engine", "PulseReserve.Modules.Reservation", ["Per-Event Seat Inventory", "FOR UPDATE SKIP LOCKED", "Zero Double-Booking Guarantee", "Fixed 10-Min Expiry TTL"], 780, 350, 350),
        ("Payments & Checkout", "PulseReserve.Modules.Payments", ["Independent State Machine", "Stripe & Paymob Gateway", "Automated Reconciliation", "Manual & Auto Refunds"], 1160, 350, 340),
        ("Notifications", "PulseReserve.Modules.Notifications", ["Transactional Email / SMS", "Async Event Dispatcher", "Dev Adapter / Mailhog", "Template Rendering"], 1530, 350, 310),
    ]

    for mtitle, msub, items, mx, my, mw in modules:
        mh = 340
        # Module Card
        draw.rounded_rectangle([mx, my, mx + mw, my + mh], radius=14, fill=(30, 41, 59, 220), outline=(99, 102, 241, 160), width=2)
        draw.rounded_rectangle([mx, my, mx + mw, my + 48], radius=14, fill=(49, 46, 129, 255))
        draw.text((mx + 16, my + 8), mtitle, fill=(255, 255, 255), font=font_bullet_title)
        draw.text((mx + 16, my + 30), msub, fill=(199, 210, 254), font=font_small)

        iy = my + 65
        for item in items:
            draw.text((mx + 20, iy), "• " + item, fill=(226, 232, 240), font=font_bullet_desc)
            iy += 40

        # Contracts indicator
        draw.rounded_rectangle([mx + 16, my + mh - 55, mx + mw - 16, my + mh - 16], radius=8, fill=(15, 23, 42, 200), outline=(51, 65, 85, 150), width=1)
        draw.text((mx + 26, my + mh - 44), "Public Contracts Only (No Leakage)", fill=(56, 189, 248), font=font_small)

    # 3. Transactional Outbox & In-Process Integration Layer
    draw.rounded_rectangle([80, 720, 1840, 810], radius=14, fill=(17, 24, 39, 240), outline=(234, 179, 8, 160), width=2)
    draw.text((110, 735), "TRANSACTIONAL OUTBOX PATTERN & IN-PROCESS EVENT DISPATCHER", fill=(250, 204, 21), font=font_bullet_title)
    draw.text((110, 770), "Guarantees At-Least-Once Delivery • Atomic Persistence with Business Logic • Decouples Synchronous Reservation from Side Effects", fill=(203, 213, 225), font=font_bullet_desc)

    # 4. Authoritative Database Layer (PostgreSQL Isolated Schemas)
    draw.rounded_rectangle([80, 840, 1840, 1010], radius=16, fill=(15, 23, 42, 240), outline=(59, 130, 246, 180), width=2)
    draw.text((110, 855), "AUTHORITATIVE PERSISTENCE LAYER: PostgreSQL (Isolated Schema-Per-Module)", fill=(96, 165, 250), font=font_bullet_title)

    db_schemas = [
        ("Schema: identity", "users, credentials, session_tokens", 110),
        ("Schema: catalog", "venues, layouts, sections, rows, seats, events, event_prices", 430),
        ("Schema: reservation", "event_seat_inventory, reservations, held_seats, outbox", 820),
        ("Schema: payments", "orders, attempts, reconciliation_tasks, refunds, anomalies", 1230),
        ("Schema: notifications", "notification_logs, outbox_deliveries", 1570),
    ]
    for sname, stables, sx in db_schemas:
        draw.rounded_rectangle([sx, 900, sx + 250, 985], radius=10, fill=(30, 41, 59, 200), outline=(51, 65, 85, 150), width=1)
        draw.text((sx + 14, 912), sname, fill=(56, 189, 248), font=font_tech)
        draw.text((sx + 14, 942), stables, fill=(148, 163, 184), font=font_small)

    im.convert("RGB").save(os.path.join(OUT_DIR, "06_System_Architecture_Blueprint.png"), quality=95)
    print("Architecture diagram saved successfully!")

if __name__ == "__main__":
    create_cover()
    create_architecture_diagram()
