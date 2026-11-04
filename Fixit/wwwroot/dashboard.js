(() => {
    const body = document.body;
    const role = body.dataset.role;
    const page = body.dataset.page;
    const isAdmin = role === 'admin';
    const statusOptions = ['Pending', 'Acknowledged', 'In Progress', 'Resolved', 'Rejected'];
    const priorities = ['Low', 'Normal', 'High'];
    let session;
    let profile;
    let classes = [];
    let reports = [];
    let notifications = [];
    let activeReportId = null;
    let activeGradeId = null;
    let activeSectionId = null;
    let activeFolder = 'grades';

    const byId = id => document.getElementById(id);
    const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    async function request(url, options = {}) {
        const response = await fetch(url, {
            credentials: 'same-origin',
            headers: {
                Accept: 'application/json',
                ...(options.body ? { 'Content-Type': 'application/json' } : {}),
                ...options.headers
            },
            ...options
        });
        const payload = await response.json().catch(() => ({}));
        if (response.status === 401) {
            window.location.replace('index.html');
            throw new Error('Your session has expired. Sign in again.');
        }
        if (!response.ok)
            throw new Error(payload.message || payload.detail || `The request failed (HTTP ${response.status}).`);
        return payload;
    }

    function showMessage(text, isError = false) {
        const message = byId(isAdmin ? 'admin-global-message' : 'dashboard-message');
        if (!message) return;
        message.textContent = text;
        message.classList.toggle('is-error', isError);
        message.hidden = !text;
    }

    function formatDate(value, withTime = false) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return 'Date unavailable';
        return new Intl.DateTimeFormat(undefined, withTime
            ? { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' }
            : { month: 'short', day: 'numeric', year: 'numeric' }).format(date);
    }

    function statusSlug(status) {
        return String(status || '').toLowerCase().replaceAll(' ', '-').replace(/[^a-z0-9-]/g, '');
    }

    function statusPill(status) {
        return `<span class="status-pill status-${statusSlug(status)}">${escapeHtml(status || 'Pending')}</span>`;
    }

    function updateIdentity() {
        const name = session?.username || 'Student';
        if (byId('profile-name')) byId('profile-name').textContent = name;
        if (byId('menu-name')) byId('menu-name').textContent = name;
        if (byId('profile-avatar')) byId('profile-avatar').textContent = name.trim().charAt(0).toUpperCase() || 'S';
        if (byId('settings-name')) byId('settings-name').value = name;
        const heading = byId('welcome-heading');
        if (heading) heading.textContent = `Welcome back, ${name.split(/\s+/)[0]}.`;
    }

    function updateClassStatus() {
        const element = byId('class-status');
        if (!element || !profile) return;
        const verified = profile.classVerificationStatus === 'Verified';
        if (verified) {
            element.textContent = `Class verified · ${profile.gradeLevel || ''} ${profile.section || ''}`.trim();
            element.classList.add('is-verified');
            element.hidden = false;
        } else if (profile.classVerificationStatus === 'Pending Verification') {
            element.textContent = 'Your class details are awaiting administrator verification. You can still track your own reports.';
            element.classList.remove('is-verified');
            element.hidden = false;
        } else {
            element.textContent = '';
            element.hidden = true;
        }
    }

    function getStudentReports() {
        const query = byId('report-search')?.value.trim().toLowerCase() || '';
        const status = byId('status-filter')?.value || '';
        const category = byId('category-filter')?.value || '';
        const sort = byId('sort-filter')?.value || 'newest';
        return reports
            .filter(report => !status || report.status === status)
            .filter(report => !category || report.category === category)
            .filter(report => !query || [report.title, report.location, report.category, report.description]
                .some(value => String(value || '').toLowerCase().includes(query)))
            .sort((left, right) => sort === 'oldest'
                ? new Date(left.createdAt) - new Date(right.createdAt)
                : new Date(right.createdAt) - new Date(left.createdAt));
    }

    function reportRow(report, compact = false) {
        return `<button type="button" class="report-row" data-action="open-report" data-report-id="${report.id}" aria-label="View report: ${escapeHtml(report.title)}">
            <span class="report-row-main">
                <span class="report-row-title">${escapeHtml(report.title)}</span>
                <span class="report-row-meta">${escapeHtml(report.category || 'General')} · ${escapeHtml(report.location)}${report.hasPhoto ? ' · Photo attached' : ''}</span>
            </span>
            <span class="report-row-side">${statusPill(report.status)}${compact ? `<span class="report-row-date">${formatDate(report.createdAt)}</span>` : '<span class="report-arrow" aria-hidden="true">→</span>'}</span>
        </button>`;
    }

    function emptyState(title, text, action = false) {
        return `<div class="empty-state"><strong>${escapeHtml(title)}</strong><span>${escapeHtml(text)}</span>${action
            ? '<br><button class="button button-primary" type="button" data-open-report><span class="button-plus" aria-hidden="true">+</span> Submit a Report</button>'
            : ''}</div>`;
    }

    function renderStudentReports() {
        const counts = {
            total: reports.length,
            pending: reports.filter(item => ['Pending', 'Acknowledged'].includes(item.status)).length,
            active: reports.filter(item => item.status === 'In Progress').length,
            resolved: reports.filter(item => item.status === 'Resolved').length
        };
        for (const [key, value] of Object.entries(counts)) {
            const element = byId(`stat-${key}`);
            if (element) element.textContent = value;
        }
        const recent = byId('recent-reports');
        if (recent) {
            recent.innerHTML = reports.length
                ? reports.slice(0, 4).map(report => reportRow(report, true)).join('')
                : emptyState('No reports yet', 'When you submit a maintenance report, you can follow its progress here.', true);
        }
        const list = byId('report-grid');
        if (list) {
            const visible = getStudentReports();
            if (byId('visible-report-count'))
                byId('visible-report-count').textContent = `${visible.length} ${visible.length === 1 ? 'report' : 'reports'}`;
            list.innerHTML = visible.length
                ? visible.map(report => reportRow(report)).join('')
                : reports.length
                    ? emptyState('No reports match these filters', 'Try another status or search term.')
                    : emptyState('No reports yet', 'Your submissions will stay private to your account.', true);
        }
    }

    function renderNotifications() {
        const list = byId('student-updates');
        if (!list) return;
        const relevant = notifications.filter(item => reports.some(report => report.id === item.reportId));
        list.innerHTML = relevant.length
            ? relevant.slice(0, 5).map(item => `<button class="update-item" type="button" data-action="open-notification" data-notification-id="${item.id}" data-report-id="${item.reportId}">
                <strong>${escapeHtml(item.message)}</strong>
                <time>${formatDate(item.createdAt, true)}</time>
            </button>`).join('')
            : '<p class="empty-state">There are no updates yet. When the maintenance team changes one of your reports, the update will appear here.</p>';
    }

    function setClassSections(gradeId, selectId = 'profile-section') {
        const select = byId(selectId);
        if (!select) return;
        const grade = classes.find(item => String(item.id) === String(gradeId));
        const sections = grade?.sections || [];
        select.innerHTML = sections.length
            ? `<option value="">Choose a section</option>${sections.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`
            : '<option value="">No sections configured</option>';
        select.disabled = sections.length === 0;
    }

    function populateClassChoices() {
        const gradeSelect = byId('profile-grade');
        if (!gradeSelect) return;
        gradeSelect.innerHTML = classes.length
            ? `<option value="">Choose a grade level</option>${classes.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`
            : '<option value="">Class list is not configured</option>';
        gradeSelect.disabled = classes.length === 0;
        setClassSections('');
    }

    function requireClassProfile() {
        if (isAdmin || (profile?.gradeLevelId && profile?.sectionId)) return;
        const dialog = byId('class-dialog');
        if (!dialog || dialog.open) return;
        populateClassChoices();
        const close = dialog.querySelector('.dialog-x');
        if (close) close.hidden = true;
        const copy = dialog.querySelector('.dialog-copy');
        if (!classes.length && copy)
            copy.textContent = 'The school class directory has not been configured yet. Ask an administrator to add the official grade and section names; then return here to complete your profile.';
        dialog.addEventListener('cancel', event => event.preventDefault());
        dialog.showModal();
    }

    async function openReport(reportId) {
        const dialog = byId('report-details-dialog');
        const content = byId(isAdmin ? 'admin-report-details' : 'report-details-body');
        if (!dialog || !content) return;
        content.innerHTML = '<p class="loading-state">Loading report details…</p>';
        if (isAdmin) {
            byId('admin-report-form').hidden = true;
            byId('admin-report-error').textContent = '';
        }
        dialog.showModal();
        try {
            const report = await request(`/api/reports/${reportId}`);
            activeReportId = report.id;
            const history = (report.history || []).map(entry => `<li>
                <span class="timeline-dot" aria-hidden="true"></span>
                <div><strong>${escapeHtml(entry.action)}</strong><p>${escapeHtml(entry.details)}</p>
                <time>${formatDate(entry.createdAt, true)}${entry.actor ? ` · ${escapeHtml(entry.actor)}` : ''}</time></div>
            </li>`).join('');
            content.innerHTML = `<p class="detail-kicker">REPORT FI-${String(report.id).padStart(6, '0')}</p>
                <h2 id="details-title">${escapeHtml(report.title)}</h2>
                <div class="detail-meta">${statusPill(report.status)}<span>${escapeHtml(report.category)}</span><span>${escapeHtml(report.location)}</span><span>Submitted ${formatDate(report.createdAt)}</span></div>
                ${isAdmin ? `<section class="detail-block"><h3>Submitted by</h3><p>${escapeHtml(report.reporter || 'Student')} · ${escapeHtml(report.gradeLevel || 'Class unassigned')} · ${escapeHtml(report.section || report.classVerificationStatus || 'Unassigned')}</p></section>` : ''}
                <section class="detail-block"><h3>Description</h3><p>${escapeHtml(report.description)}</p></section>
                ${report.publicUpdate ? `<section class="detail-block"><h3>Latest team update</h3><p>${escapeHtml(report.publicUpdate)}</p></section>` : ''}
                ${report.adminNotes && isAdmin ? `<section class="detail-block"><h3>Internal note</h3><p>${escapeHtml(report.adminNotes)}</p></section>` : ''}
                ${report.assignee && isAdmin ? `<section class="detail-block"><h3>Assigned to</h3><p>${escapeHtml(report.assignee)}</p></section>` : ''}
                ${report.photo ? `<section class="detail-block"><h3>Attached photo</h3><img class="detail-photo" src="${escapeHtml(report.photo)}" alt="Photo attached to ${escapeHtml(report.title)}"></section>` : ''}
                <section class="detail-block"><h3>Progress history</h3>${history
                    ? `<ol class="report-timeline">${history}</ol>`
                    : '<p>No status changes have been recorded yet.</p>'}</section>`;
            if (isAdmin) {
                byId('admin-status').value = report.status;
                byId('admin-priority').value = report.priority || 'Normal';
                byId('admin-assignee').value = report.assignee || '';
                byId('admin-public-update').value = report.publicUpdate || '';
                byId('admin-notes').value = report.adminNotes || '';
                byId('admin-report-form').hidden = false;
            }
        } catch (error) {
            content.innerHTML = `<p class="field-error" role="alert">${escapeHtml(error.message)}</p>`;
        }
    }

    async function loadStudentNotifications() {
        try {
            notifications = await request('/api/notifications');
            renderNotifications();
        } catch (error) {
            const list = byId('student-updates');
            if (list) list.innerHTML = `<p class="field-error" role="alert">${escapeHtml(error.message)}</p>`;
        }
    }

    async function loadStudent() {
        [profile, reports] = await Promise.all([request('/api/profile'), request('/api/reports')]);
        classes = profile.grades || [];
        profile = profile.profile;
        updateIdentity();
        updateClassStatus();
        renderStudentReports();
        await loadStudentNotifications();
        if (!profile?.gradeLevelId || !profile?.sectionId) requireClassProfile();
    }

    function adminBreadcrumbs() {
        const nav = byId('folder-breadcrumbs');
        if (!nav) return;
        const parts = ['<button type="button" data-folder-level="grades">All grades</button>'];
        if (activeFolder === 'unassigned') {
            parts.push('<span aria-hidden="true">/</span><span aria-current="page">Unverified submissions</span>');
            nav.innerHTML = parts.join('');
            return;
        }
        if (activeGradeId) {
            const grade = classes.find(item => item.id === activeGradeId);
            parts.push('<span aria-hidden="true">/</span>');
            parts.push(activeSectionId
                ? `<button type="button" data-folder-level="sections">${escapeHtml(grade?.name || 'Grade level')}</button>`
                : `<span aria-current="page">${escapeHtml(grade?.name || 'Grade level')}</span>`);
        }
        if (activeSectionId) {
            const grade = classes.find(item => item.id === activeGradeId);
            const section = grade?.sections.find(item => item.id === activeSectionId);
            parts.push('<span aria-hidden="true">/</span>');
            parts.push(activeFolder === 'reports'
                ? `<span aria-current="page">${escapeHtml(section?.name || 'Section')}</span>`
                : `<span aria-current="page">Unverified submissions</span>`);
        }
        nav.innerHTML = parts.join('');
    }

    function folderRow({ id, name, count, subtitle, action, icon = '▰' }) {
        return `<button type="button" class="folder-row" data-folder-action="${action}" data-folder-id="${id}">
            <span class="folder-icon" aria-hidden="true">${icon}</span>
            <span><strong>${escapeHtml(name)}</strong><small>${escapeHtml(subtitle)}</small></span>
            <span class="folder-count">${count} ${count === 1 ? 'report' : 'reports'}</span><span class="folder-chevron" aria-hidden="true">›</span>
        </button>`;
    }

    function renderAdminReportTable(items) {
        if (!items.length)
            return emptyState('No reports in this folder', 'Reports submitted by verified students in this section will appear here.');
        return `<table class="admin-report-list"><thead><tr><th>Report</th><th>Submitted by</th><th>Status</th><th>Location · date</th></tr></thead><tbody>
            ${items.map(report => `<tr><td><button type="button" data-action="open-report" data-report-id="${report.id}">${escapeHtml(report.title)}</button></td>
                <td>${escapeHtml(report.reporter || 'Student')}</td><td>${statusPill(report.status)}</td>
                <td>${escapeHtml(report.location)} · ${formatDate(report.createdAt)}</td></tr>`).join('')}
        </tbody></table>`;
    }

    async function loadGradeFolders() {
        activeFolder = 'grades';
        activeGradeId = null;
        activeSectionId = null;
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = true;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading grade folders…</p>';
        const [grades, unassigned] = await Promise.all([
            request('/api/admin/report-folders/grades'),
            request('/api/admin/report-folders/unassigned')
        ]);
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><h2>Grade levels</h2><span class="record-count">${grades.length} configured</span></div>
            ${grades.length ? grades.map(grade => folderRow({
                id: grade.id, name: grade.name, count: grade.reportCount,
                subtitle: `${grade.sectionCount} ${grade.sectionCount === 1 ? 'section' : 'sections'}`,
                action: 'grade', icon: '▰'
            })).join('') : emptyState('No grade levels configured', 'Add the school’s official grade levels in Class directory before opening report folders.')}
            <div class="folder-unassigned"><div class="folder-section-heading"><h2>Reports outside class folders</h2><span class="record-count">${unassigned.length} ${unassigned.length === 1 ? 'report' : 'reports'}</span></div>
            ${folderRow({ id: 0, name: 'Unassigned reports', count: unassigned.length, subtitle: 'Unverified student reports and maintenance-created reports', action: 'unassigned', icon: '○' })}</div>`;
        adminBreadcrumbs();
    }

    async function loadSectionFolders(gradeId) {
        activeFolder = 'sections';
        activeGradeId = Number(gradeId);
        activeSectionId = null;
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = true;
        byId('folder-content').innerHTML = '<p class="loading-state">Opening grade folder…</p>';
        const result = await request(`/api/admin/report-folders/grades/${activeGradeId}/sections`);
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><h2>${escapeHtml(result.gradeLevel)} sections</h2><span class="record-count">${result.sections.length} ${result.sections.length === 1 ? 'section' : 'sections'}</span></div>
            ${result.sections.length ? result.sections.map(section => folderRow({
                id: section.id, name: section.name, count: section.reportCount,
                subtitle: 'Verified student reports', action: 'section', icon: '▱'
            })).join('') : emptyState('No sections configured', 'Add the official sections for this grade in Class directory.')}`;
        adminBreadcrumbs();
    }

    async function loadSectionReports() {
        if (!activeSectionId) return;
        activeFolder = 'reports';
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = false;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading reports…</p>';
        const params = new URLSearchParams({
            search: byId('folder-search').value.trim(),
            status: byId('folder-status').value,
            category: byId('folder-category').value,
            sort: byId('folder-sort').value
        });
        const result = await request(`/api/admin/report-folders/sections/${activeSectionId}/reports?${params}`);
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><h2>${escapeHtml(result.gradeLevel)} · ${escapeHtml(result.section)}</h2><span class="record-count">${result.reports.length} ${result.reports.length === 1 ? 'report' : 'reports'}</span></div>
            ${renderAdminReportTable(result.reports)}`;
    }

    async function loadUnassignedReports() {
        activeFolder = 'unassigned';
        activeGradeId = null;
        activeSectionId = 0;
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = true;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading unverified reports…</p>';
        const items = await request('/api/admin/report-folders/unassigned');
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><h2>Unverified student submissions</h2><span class="record-count">${items.length} ${items.length === 1 ? 'report' : 'reports'}</span></div>
            ${items.length ? `<table class="admin-report-list"><thead><tr><th>Report</th><th>Submitted by</th><th>Class submission</th><th>Status · date</th></tr></thead><tbody>
                ${items.map(report => `<tr><td><button type="button" data-action="open-report" data-report-id="${report.id}">${escapeHtml(report.title)}</button></td>
                <td>${escapeHtml(report.reporter)}</td><td>${escapeHtml(report.GradeLevel || 'Class unassigned')} · ${escapeHtml(report.Section || report.classVerificationStatus)}</td>
                <td>${statusPill(report.status)} · ${formatDate(report.createdAt)}</td></tr>`).join('')}
            </tbody></table>` : emptyState('No unverified submissions', 'New reports from students awaiting class verification will appear here.')}`;
    }

    async function loadAdminOverview() {
        const data = await request('/api/admin/overview');
        byId('overview-total').textContent = data.total;
        byId('overview-pending').textContent = data.pending + data.acknowledged;
        byId('overview-progress').textContent = data.inProgress;
        byId('overview-resolved').textContent = data.resolved;
        byId('overview-grades').innerHTML = data.reportsByGrade.length
            ? data.reportsByGrade.map(item => `<button class="overview-grade-row" type="button" data-open-grade="${escapeHtml(item.gradeLevel)}"><strong>${escapeHtml(item.gradeLevel)}</strong><span>${item.reportCount} ${item.reportCount === 1 ? 'report' : 'reports'} <span aria-hidden="true">→</span></span></button>`).join('')
            : '<p class="empty-state">Verified grade-level report totals will appear once students are confirmed and reports are submitted.</p>';
        byId('overview-activity').innerHTML = data.recentActivity.length
            ? data.recentActivity.map(item => `<li><strong>${escapeHtml(item.action)} · ${escapeHtml(item.reportTitle || `FI-${item.reportId}`)}</strong><p>${escapeHtml(item.details)}</p><small>${escapeHtml(item.actor || 'fixIt')} · ${formatDate(item.createdAt, true)}</small></li>`).join('')
            : '<li class="empty-state">No report activity has been recorded yet.</li>';
    }

    async function loadClassDirectory() {
        const directory = await request('/api/classes');
        classes = directory.grades || [];
        const gradeSelect = byId('section-grade');
        if (gradeSelect) {
            const previous = gradeSelect.value;
            gradeSelect.innerHTML = `<option value="">Choose a grade</option>${classes.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`;
            if (classes.some(item => String(item.id) === previous)) gradeSelect.value = previous;
        }
        byId('directory-list').innerHTML = classes.length
            ? classes.map(grade => `<section class="directory-grade">
                <div class="directory-grade-head"><strong>${escapeHtml(grade.name)}</strong><span class="directory-grade-actions"><small>${grade.sections.length} ${grade.sections.length === 1 ? 'section' : 'sections'}</small><button type="button" class="directory-edit" data-edit-grade="${grade.id}" aria-label="Rename ${escapeHtml(grade.name)}">Rename</button></span></div>
                <div class="directory-section-list">${grade.sections.length
                    ? grade.sections.map(section => `<button type="button" class="directory-section-tag" data-edit-section="${section.id}" aria-label="Rename ${escapeHtml(section.name)}">${escapeHtml(section.name)} <span>Edit</span></button>`).join('')
                    : '<span class="label-muted">No sections added yet.</span>'}</div>
            </section>`).join('')
            : emptyState('The class directory is empty', 'Add official grade levels and sections here. They will be used in registration and report folders.');
        return classes;
    }

    async function loadVerifications() {
        const [students, directory] = await Promise.all([
            request('/api/admin/class-verifications'),
            loadClassDirectory()
        ]);
        const count = byId('verification-count');
        count.hidden = !students.length;
        count.textContent = students.length;
        const list = byId('verification-list');
        if (!students.length) {
            list.innerHTML = emptyState('No student classes need review', 'New class submissions will appear here until verified.');
        } else {
            renderClassAssignmentForms(list, students, directory, false);
        }
        await loadVerifiedStudents('', directory);
    }

    function renderClassAssignmentForms(list, students, directory, isVerified) {
        list.innerHTML = students.map(student => `<form class="verification-row" data-student-id="${student.id}">
            <div class="verification-student"><strong>${escapeHtml(student.username)}</strong><small>${escapeHtml(student.email || 'No email')} · ${student.reportCount} ${student.reportCount === 1 ? 'report' : 'reports'}</small>
                <span class="pending-label">${isVerified ? 'Verified' : `${escapeHtml(student.classVerificationStatus)}${student.gradeLevel ? ` · ${escapeHtml(student.gradeLevel)} ${escapeHtml(student.section || '')}` : ''}`}</span></div>
            <label>Grade<select name="gradeLevelId" required><option value="">Choose a grade</option>${directory.map(grade => `<option value="${grade.id}"${grade.id === student.gradeLevelId ? ' selected' : ''}>${escapeHtml(grade.name)}</option>`).join('')}</select></label>
            <label>Section<select name="sectionId" required disabled><option value="">Choose a grade first</option></select></label>
            <button class="button ${isVerified ? 'button-outline' : 'button-primary'}" type="submit"${directory.length ? '' : ' disabled'}>${isVerified ? 'Update class' : 'Verify class'}</button>
        </form>`).join('');
        list.querySelectorAll('.verification-row').forEach(form => {
            const gradeSelect = form.elements.gradeLevelId;
            const sectionSelect = form.elements.sectionId;
            const setOptions = () => {
                const grade = directory.find(item => String(item.id) === gradeSelect.value);
                const sections = grade?.sections || [];
                const oldValue = sectionSelect.value || String(students.find(item => String(item.id) === form.dataset.studentId)?.sectionId || '');
                sectionSelect.innerHTML = sections.length
                    ? `<option value="">Choose a section</option>${sections.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`
                    : '<option value="">No sections configured</option>';
                sectionSelect.disabled = !sections.length;
                if (sections.some(item => String(item.id) === oldValue)) sectionSelect.value = oldValue;
            };
            gradeSelect.addEventListener('change', setOptions);
            setOptions();
        });
    }

    async function loadVerifiedStudents(search = '', directory = classes) {
        const list = byId('verified-students-list');
        if (!list) return;
        list.innerHTML = '<p class="loading-state">Loading verified students…</p>';
        const students = await request(`/api/admin/students?search=${encodeURIComponent(search)}`);
        list.innerHTML = students.length
            ? ''
            : emptyState(search ? 'No matching verified students' : 'No verified student accounts', 'Verified class placements can be corrected here without changing report ownership or dates.');
        if (students.length) renderClassAssignmentForms(list, students, directory, true);
    }

    async function saveClassAssignment(event) {
        const form = event.target.closest('.verification-row');
        if (!form) return;
        event.preventDefault();
        const button = form.querySelector('[type="submit"]');
        button.disabled = true;
        try {
            await request(`/api/admin/users/${form.dataset.studentId}/class`, {
                method: 'PATCH',
                body: JSON.stringify({
                    gradeLevelId: Number(form.elements.gradeLevelId.value),
                    sectionId: Number(form.elements.sectionId.value)
                })
            });
            showMessage(form.closest('#verified-students-list')
                ? 'Student class assignment updated.'
                : 'Student class assignment verified.');
            await loadVerifications();
        } catch (error) {
            showMessage(error.message, true);
            button.disabled = false;
        }
    }

    async function showAdminPanel(name) {
        document.querySelectorAll('[data-panel]').forEach(panel => { panel.hidden = panel.dataset.panel !== name; });
        document.querySelectorAll('[data-view]').forEach(button => button.classList.toggle('is-active', button.dataset.view === name));
        if (name === 'overview') await loadAdminOverview();
        if (name === 'reports') await loadGradeFolders();
        if (name === 'verification') await loadVerifications();
        if (name === 'directory') await loadClassDirectory();
    }

    async function loadAdmin() {
        await loadClassDirectory();
        updateIdentity();
        await showAdminPanel('overview');
    }

    function setupAccountMenu() {
        const toggle = byId('profile-toggle');
        if (!toggle) return;
        toggle.addEventListener('click', () => {
            const menu = byId('profile-menu');
            const expanded = toggle.getAttribute('aria-expanded') === 'true';
            toggle.setAttribute('aria-expanded', String(!expanded));
            menu.hidden = expanded;
        });
        document.addEventListener('click', event => {
            if (!event.target.closest('.account-menu')) {
                byId('profile-menu').hidden = true;
                toggle.setAttribute('aria-expanded', 'false');
            }
        });
        document.querySelector('[data-open-settings]')?.addEventListener('click', () => {
            byId('profile-menu').hidden = true;
            toggle.setAttribute('aria-expanded', 'false');
            byId('settings-error').textContent = '';
            byId('settings-dialog').showModal();
        });
        document.querySelector('[data-logout]')?.addEventListener('click', async event => {
            const button = event.currentTarget;
            button.disabled = true;
            try {
                await request('/api/logout', { method: 'POST' });
                window.location.replace('index.html');
            } catch (error) {
                showMessage(error.message, true);
                button.disabled = false;
            }
        });
        document.querySelectorAll('[data-close-dialog]').forEach(button => {
            button.addEventListener('click', () => button.closest('dialog').close());
        });
        document.querySelectorAll('.app-dialog').forEach(dialog => dialog.addEventListener('click', event => {
            if (event.target === dialog) dialog.close();
        }));
    }

    function setupReportActions() {
        document.addEventListener('click', event => {
            if (!event.target.closest('[data-open-report]')) return;
            if (!isAdmin && (!profile?.gradeLevelId || !profile?.sectionId)) {
                requireClassProfile();
                return;
            }
            byId('report-error').textContent = '';
            byId('report-dialog').showModal();
        });

        const imageInput = byId('report-image');
        imageInput?.addEventListener('change', () => {
            const preview = byId('image-preview');
            preview.replaceChildren();
            const file = imageInput.files[0];
            if (!file) return;
            if (!['image/jpeg', 'image/png', 'image/gif', 'image/webp'].includes(file.type) || file.size > 1_048_576) {
                byId('report-error').textContent = 'Choose a JPEG, PNG, GIF, or WebP image smaller than 1 MB.';
                imageInput.value = '';
                return;
            }
            const image = document.createElement('img');
            image.alt = 'Selected maintenance issue photo preview';
            image.src = URL.createObjectURL(file);
            preview.append(image);
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.textContent = 'Remove photo';
            remove.addEventListener('click', () => { imageInput.value = ''; preview.replaceChildren(); });
            preview.append(remove);
        });

        byId('report-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const submit = form.querySelector('[type="submit"]');
            const file = imageInput.files[0];
            byId('report-error').textContent = '';
            submit.disabled = true;
            try {
                const image = file ? await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.addEventListener('load', () => resolve(reader.result));
                    reader.addEventListener('error', () => reject(new Error('The image could not be read.')));
                    reader.readAsDataURL(file);
                }) : null;
                const fields = new FormData(form);
                const result = await request('/api/reports', {
                    method: 'POST',
                    body: JSON.stringify({
                        title: fields.get('title'),
                        location: fields.get('location'),
                        category: fields.get('category'),
                        description: fields.get('description'),
                        image
                    })
                });
                form.reset();
                byId('image-preview').replaceChildren();
                byId('report-dialog').close();
                if (isAdmin) {
                    if (activeFolder === 'grades') await loadGradeFolders();
                    if (activeFolder === 'reports') await loadSectionReports();
                    if (activeFolder === 'unassigned') await loadUnassignedReports();
                    await loadAdminOverview();
                    showMessage(`Report FI-${String(result.id).padStart(6, '0')} created.`);
                } else {
                    reports = await request('/api/reports');
                    renderStudentReports();
                    showMessage(`Report FI-${String(result.id).padStart(6, '0')} submitted. It is now in My Reports.`);
                    await loadStudentNotifications();
                }
            } catch (error) {
                byId('report-error').textContent = error.message;
            } finally {
                submit.disabled = false;
            }
        });
    }

    function setupStudentActions() {
        byId('profile-grade')?.addEventListener('change', event => setClassSections(event.target.value));
        byId('class-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('class-error').textContent = '';
            try {
                await request('/api/profile/class', {
                    method: 'PATCH',
                    body: JSON.stringify({
                        gradeLevelId: Number(byId('profile-grade').value),
                        sectionId: Number(byId('profile-section').value)
                    })
                });
                profile.gradeLevelId = Number(byId('profile-grade').value);
                profile.sectionId = Number(byId('profile-section').value);
                profile.classVerificationStatus = 'Pending Verification';
                profile.gradeLevel = classes.find(item => item.id === profile.gradeLevelId)?.name || '';
                profile.section = classes.find(item => item.id === profile.gradeLevelId)?.sections
                    .find(item => item.id === profile.sectionId)?.name || '';
                byId('class-dialog').close();
                const close = byId('class-dialog').querySelector('.dialog-x');
                if (close) close.hidden = false;
                updateClassStatus();
                showMessage('Your class details are waiting for administrator verification.');
            } catch (error) {
                byId('class-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });

        byId('report-search')?.addEventListener('input', renderStudentReports);
        ['status-filter', 'category-filter', 'sort-filter'].forEach(id => byId(id)?.addEventListener('change', renderStudentReports));
        byId('report-filters')?.addEventListener('submit', event => event.preventDefault());
        byId('clear-filters')?.addEventListener('click', () => {
            ['report-search', 'status-filter', 'category-filter'].forEach(id => { if (byId(id)) byId(id).value = ''; });
            if (byId('sort-filter')) byId('sort-filter').value = 'newest';
            renderStudentReports();
        });
    }

    function setupAdminActions() {
        document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {
            showAdminPanel(button.dataset.view).catch(error => showMessage(error.message, true));
        }));
        document.querySelector('[data-view-button="reports"]')?.addEventListener('click', () => {
            showAdminPanel('reports').catch(error => showMessage(error.message, true));
        });
        document.addEventListener('click', event => {
            const gradeLink = event.target.closest('[data-open-grade]');
            if (!gradeLink) return;
            showAdminPanel('reports').then(async () => {
                const grade = classes.find(item => item.name === gradeLink.dataset.openGrade);
                if (grade) await loadSectionFolders(grade.id);
            }).catch(error => showMessage(error.message, true));
        });
        byId('folder-breadcrumbs').addEventListener('click', event => {
            const button = event.target.closest('[data-folder-level]');
            if (!button) return;
            const action = button.dataset.folderLevel;
            const work = action === 'grades' ? loadGradeFolders() : loadSectionFolders(activeGradeId);
            work.catch(error => showMessage(error.message, true));
        });
        byId('folder-content').addEventListener('click', event => {
            const button = event.target.closest('[data-folder-action]');
            if (!button) return;
            const action = button.dataset.folderAction;
            const work = action === 'grade'
                ? loadSectionFolders(Number(button.dataset.folderId))
                : action === 'section'
                    ? (activeSectionId = Number(button.dataset.folderId), loadSectionReports())
                    : loadUnassignedReports();
            work.catch(error => showMessage(error.message, true));
        });
        ['folder-search', 'folder-status', 'folder-category', 'folder-sort'].forEach(id => {
            const input = byId(id);
            input?.addEventListener(input.tagName === 'INPUT' ? 'input' : 'change', () => {
                if (activeSectionId) loadSectionReports().catch(error => showMessage(error.message, true));
            });
        });
        byId('folder-clear')?.addEventListener('click', () => {
            byId('folder-search').value = '';
            byId('folder-status').value = '';
            byId('folder-category').value = '';
            byId('folder-sort').value = 'newest';
            loadSectionReports().catch(error => showMessage(error.message, true));
        });
        byId('verification-list').addEventListener('submit', saveClassAssignment);
        byId('verified-students-list').addEventListener('submit', saveClassAssignment);
        let studentSearchTimer;
        byId('verified-students-search').addEventListener('input', event => {
            window.clearTimeout(studentSearchTimer);
            studentSearchTimer = window.setTimeout(() => {
                loadVerifiedStudents(event.target.value.trim()).catch(error => showMessage(error.message, true));
            }, 250);
        });
        byId('grade-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('grade-error').textContent = '';
            try {
                await request('/api/admin/grade-levels', {
                    method: 'POST',
                    body: JSON.stringify({ name: byId('grade-name').value })
                });
                byId('grade-form').reset();
                await loadClassDirectory();
                showMessage('Grade level added to the class directory.');
            } catch (error) {
                byId('grade-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('section-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('section-error').textContent = '';
            try {
                await request(`/api/admin/grade-levels/${byId('section-grade').value}/sections`, {
                    method: 'POST',
                    body: JSON.stringify({ name: byId('section-name').value })
                });
                byId('section-form').reset();
                await loadClassDirectory();
                showMessage('Section added to the class directory.');
            } catch (error) {
                byId('section-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('directory-list').addEventListener('click', async event => {
            const gradeButton = event.target.closest('[data-edit-grade]');
            const sectionButton = event.target.closest('[data-edit-section]');
            const grade = gradeButton
                ? classes.find(item => item.id === Number(gradeButton.dataset.editGrade))
                : null;
            const section = sectionButton
                ? classes.flatMap(item => item.sections).find(item => item.id === Number(sectionButton.dataset.editSection))
                : null;
            const current = grade?.name || section?.name;
            if (!current) return;
            const name = window.prompt('Enter the official name:', current)?.trim();
            if (!name || name === current) return;
            try {
                await request(grade
                    ? `/api/admin/grade-levels/${grade.id}`
                    : `/api/admin/sections/${section.id}`, {
                    method: 'PATCH',
                    body: JSON.stringify({ name })
                });
                await loadClassDirectory();
                showMessage('Class directory name updated.');
            } catch (error) {
                showMessage(error.message, true);
            }
        });
        byId('admin-report-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('admin-report-error').textContent = '';
            try {
                await request(`/api/reports/${activeReportId}`, {
                    method: 'PATCH',
                    body: JSON.stringify({
                        status: byId('admin-status').value,
                        priority: byId('admin-priority').value,
                        assignee: byId('admin-assignee').value,
                        publicUpdate: byId('admin-public-update').value,
                        adminNotes: byId('admin-notes').value
                    })
                });
                showMessage('Report updates saved.');
                await openReport(activeReportId);
                if (activeFolder === 'reports') await loadSectionReports();
                if (activeFolder === 'unassigned') await loadUnassignedReports();
                await loadAdminOverview();
            } catch (error) {
                byId('admin-report-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('delete-report').addEventListener('click', async () => {
            if (!activeReportId || !window.confirm('Delete this report permanently? This action cannot be undone.')) return;
            const button = byId('delete-report');
            button.disabled = true;
            try {
                await request(`/api/reports/${activeReportId}`, { method: 'DELETE' });
                byId('report-details-dialog').close();
                showMessage('Report deleted.');
                if (activeFolder === 'reports') await loadSectionReports();
                if (activeFolder === 'unassigned') await loadUnassignedReports();
                await loadAdminOverview();
            } catch (error) {
                showMessage(error.message, true);
            } finally {
                button.disabled = false;
            }
        });
    }

    function setupSettings() {
        byId('settings-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('settings-error').textContent = '';
            try {
                const result = await request('/api/profile', {
                    method: 'POST',
                    body: JSON.stringify({ username: byId('settings-name').value.trim() })
                });
                session.username = result.username;
                updateIdentity();
                byId('settings-dialog').close();
                showMessage('Your profile has been updated.');
            } catch (error) {
                byId('settings-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
    }

    async function initialize() {
        document.querySelectorAll('.page-message').forEach(message => { message.hidden = true; });
        try {
            session = await request('/api/me');
            if (!['user', 'admin'].includes(session.role))
                throw new Error('Your account role is not recognized.');
            if (session.role !== role) {
                window.location.replace(session.role === 'admin' ? 'dashboard_admin.html' : 'dashboard_user.html');
                return;
            }
            setupAccountMenu();
            setupSettings();
            setupReportActions();
            if (isAdmin) {
                setupAdminActions();
                await loadAdmin();
            } else {
                setupStudentActions();
                await loadStudent();
            }
        } catch (error) {
            showMessage(`Could not load your workspace: ${error.message}`, true);
        }
    }

    document.addEventListener('click', async event => {
        const open = event.target.closest('[data-action="open-report"]');
        if (open) {
            await openReport(Number(open.dataset.reportId));
            return;
        }
        const notification = event.target.closest('[data-action="open-notification"]');
        if (notification && !isAdmin) {
            try {
                await request(`/api/notifications/${notification.dataset.notificationId}/read`, { method: 'POST' });
                await openReport(Number(notification.dataset.reportId));
            } catch (error) {
                showMessage(error.message, true);
            }
        }
    });

    if (page === 'admin') byId('admin-global-message').hidden = true;
    initialize();
})();
